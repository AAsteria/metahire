using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;



namespace Byn.Awrtc.Unity
{
    /// <summary>
    /// The FrameProcessor can be used to consume & process IFrame events created by ICall 
    /// to convert them into Texture2D for usage in UI or on 3D objects. 
    /// 
    /// Use Process to convert any frames received into Texture2D. Results are returned via the FrameProcessed event 
    /// during the Unity Update or LateUpdate loop. 
    /// Call FreeConnection once a user disconnected (CallEnded event) to ensure the frame processor can release all
    /// textures & buffers associated with that users.
    /// 
    /// Note all Texture2D objects returned by this class are destroyed or reused once a new Texture2D is returned for the same user. 
    /// Destroying FrameProcessor will also destroy all textures.
    /// 
    /// </summary>
    public class FrameProcessor : MonoBehaviour
    {
        //TODO:move to configuration
        //NOTE: DO NOT USE. THIS DOES NOT YET WORK PROPERLY.
        public static bool PARALLEL_PROCESSING = false;

        //Ignore. Not yet used.
        //this forces a unity job to complete once a second frame is delivered.
        //TODO: This does not yet work because we currently always force frames at the
        //end of the unity update loop
        public static bool FORCE_FRAMES = true;

        /// <summary>
        /// Known connections and associated user specific frame processors
        /// </summary>
        private Dictionary<ConnectionId, UserFrameProcessor> mUserFrameProcessors = new Dictionary<ConnectionId, UserFrameProcessor>();

        /// <summary>
        /// For sanity checks. If true the user callbacks are running right now
        /// and no other methods should be called during this process.
        /// </summary>
        private bool mIsInDelivery = false;


        /// <summary>
        /// This event handler will return any processed frames once the conversion is completed. 
        /// Use ProcessedFrame.MetaData.ConnecitonId to identify to which connection is frame belongs.
        /// </summary>
        public event Action<ProcessedFrame> FrameProcessed;

        /// <summary>
        /// This will process the frame and consume FrameUpdateEventArgs.  
        /// Processed frames are later returned via FrameProcessed.
        /// Note not all frames might trigger FrameProcessed to be called.
        /// If frames are received too quick for processing frames are dropped.
        /// 
        /// Warning do not access FrameUpdateEventArgs after this call! It's contents might be
        /// disposed or accessed in a parallel thread!
        /// </summary>
        /// <param name="args">
        /// Event generated by ICall to process and convert into a Texture2D.
        /// </param>
        public void Process(FrameUpdateEventArgs args)
        {
            if (args == null)
            {
                throw new ArgumentNullException("frame can't be null");
            }
            if (FrameProcessed == null)
            {
                throw new ArgumentNullException("FrameProcessed event handler not set");
            }
            if (mIsInDelivery)
            {
                throw new InvalidOperationException("Process can't be called while frames are delivered!");
            }

            UserFrameProcessor processor;
            if(mUserFrameProcessors.TryGetValue(args.ConnectionId, out processor) == false)
            {
                processor = new UserFrameProcessor();
                mUserFrameProcessors[args.ConnectionId] = processor;
            }
            if (processor.IsBusy)
            {
                if(FORCE_FRAMES)
                {
                    //force the last frame out if the processor is still busy
                    var results = processor.Complete();
                    TriggerEvent(results);
                }
                else
                {
                    Debug.LogWarning("Still processing last frame. Dropping frame for connection " + args.ConnectionId);
                    args.Frame.Dispose();
                    return;
                }

            }

            processor.Process(args);
            //if parallel processing is disabled we expect any task to be completed immediately
            if (PARALLEL_PROCESSING == false)
                Complete();
        }

        /// <summary>
        /// Call to free all Textures associated with the connection ID.
        /// This includes any textures the FrameProcessed event might have returned in the past!
        /// 
        /// </summary>
        /// <param name="id"></param>
        public void FreeConnection(ConnectionId id)
        {
            UserFrameProcessor processor = null;
            if (mUserFrameProcessors.TryGetValue(id, out processor))
            {
                mUserFrameProcessors.Remove(id);
                processor.Cleanup();
            }
        }
        /// <summary>
        /// Frees all connections and resets the FrameProcessor to its state 
        /// after creation.
        /// </summary>
        public void FreeAll()
        {
            this.Cleanup();
        }


        private void LateUpdate()
        {
            if(PARALLEL_PROCESSING)
                Complete();
        }

        private void Complete()
        {
            foreach (var processor in mUserFrameProcessors.Values)
            {
                //TODO: This should be IsDone to
                //allow multiple frames for processing but
                //this is buggy at the moment
                if (processor.IsBusy)
                {
                    var results = processor.Complete();
                    TriggerEvent(results);
                }
            }
        }

        private void TriggerEvent(ProcessedFrame frame)
        {
            mIsInDelivery = true;
            try
            {
                if(FrameProcessed != null)
                    FrameProcessed(frame);
            }
            catch(Exception e)
            {
                Debug.LogError("Usercode triggered exception:");
                Debug.LogException(e);
            }
            //call user code
            mIsInDelivery = false;
        }

        private void Cleanup()
        {
            //TODO: We might have to force out any busy frames before shutting down
            //otherwise we leak memory on exit
            var values = mUserFrameProcessors.Values;
            Debug.Log("Cleaning up " + values.Count + " processors");
            foreach (var processor in values)
            {
                processor.Cleanup();
            }
            mUserFrameProcessors.Clear();
        }

        private void OnDestroy()
        {
            this.Cleanup();
        }
    }

    /// <summary>
    /// Result of a processed IFrame. 
    /// </summary>
    public class ProcessedFrame
    {
        /// <summary>
        /// Texture2D of the processed IFrame. 
        /// NOTE: This instance is only valid until the next frame is received for that specific connection.
        /// If another frame is received using the same MetaData.ConnectionId the old texture
        /// must not be accessed anymore as it could be reused or destroyed to free up memory.
        /// A Texture2D is also destroyed if the FrameProcessor is destroyed.
        /// 
        /// Use Graphics.CopyTexture to create a new instance if a Texture2D is needed for longer.
        /// </summary>
        public Texture2D texture;

        /// <summary>
        /// This value is usually null and indicates the result can be accessed using the usual 
        /// </summary>
        public string material;

        /// <summary>
        /// Meta data associated with this frame.
        /// </summary>
        public FrameMetaData MetaData;
    }
    /// <summary>
    /// Processes frames related to a specific user / connection. 
    /// This allows reusing Texture2D objects as width & height rarely changes.
    /// 
    /// Do not use directly. This class will be changed without warning.
    /// </summary>
    internal class UserFrameProcessor
    {
        //Texture delivered and in use right now
        Texture2D mDelivered;
        /// <summary>
        /// Max textures that are currently freed. 
        /// </summary>
        private readonly int mFreedMax = 3;
        //Textures used previously that are ready for reuse 
        List<Texture2D> mFreed = new List<Texture2D>();

        /// <summary>
        /// Converter currently used or for the last frame. Converters are reused
        /// for future frames to allow reusing any temporary textures / buffers
        /// and avoid creating garbage.
        /// </summary>
        private AFrameConverter mConverter = null;

        private FrameUpdateEventArgs mArgs = null;
        private ProcessedFrame mResults = null;

        private bool mIsBusy = false;
        /// <summary>
        /// Returns true after Process is called and
        /// until Complete() ended.
        /// </summary>
        public bool IsBusy
        {
            get
            {
                return mIsBusy;
            }
        }
        /// <summary>
        /// Checks the IsDone flag of the converter.
        /// This might be true even if Complete was not yet called.
        /// </summary>
        public bool IsDone
        {
            get
            {
                if (mConverter != null && mConverter.IsDone)
                    return true;
                return false;
            }
        }


        /// <summary>
        /// Set to true to get a regular debug printout with FPS, resolution, formats & converters in use for each
        /// active media stream
        /// </summary>
        public bool DEBUG_LOG = false;
        private int mDebugFpsCounter = 0;
        private float mDebugFpsStartTime = 0;
        /// <summary>
        /// 
        /// </summary>
        private readonly float mDebugFpsSampleTime = 5;


        public UserFrameProcessor()
        {
            mDebugFpsStartTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Starts the processing for a specific frame.
        /// This might run in parallel until completion is forced via 
        /// Complete().
        /// </summary>
        /// <param name="args">
        /// Frame event received from ICall.
        /// </param>
        public void Process(FrameUpdateEventArgs args)
        {
            mIsBusy = true;
            IFrame frame = args.Frame;
            //no converter yet or unsupported frames are delivered? Find a new one.
            if (mConverter == null || mConverter.IsValidInput(frame) == false)
            {
                mConverter = PickConverter(frame);
            }
            //In case PickConverter did not find a valid converter
            if (mConverter == null)
            {
                throw new ArgumentException("Unable to find a suitable converter for format " + frame.Format + " and type " + frame.GetType().Name);
            }
            //Sanity check. If this triggers there is a bug in PickConverter 
            if (mConverter.IsValidInput(frame) == false)
            {
                //We could support switching converters here if this is ever needed. 
                throw new InvalidOperationException("IFrame can not be processed by current converter! format " + frame.Format + " type " + frame.GetType().Name);
            }
            mArgs = args;
            mResults = new ProcessedFrame();
            mResults.MetaData = new FrameMetaData(args);
            mResults.material = mConverter.MaterialName;

            Texture2D processing = ReuseTexture();
            mConverter.Allocate(frame, ref processing);

            //Start conversion
            mConverter.Convert();
        }

        public static int GetMem()
        {
            //round down to MB for simplicity
            ulong mem = Texture.currentTextureMemory / 1024 / 1024;
            return (int)mem;
        }

        /// <summary>
        /// Completes the processing step. If the converter is not yet done this will
        /// pause execution until it finished.
        /// </summary>
        /// <returns>
        /// Returns the processed frame.
        /// </returns>
        public ProcessedFrame Complete()
        {

            mResults.texture = mConverter.Complete();

            //store old texture for reuse
            if (mDelivered != null)
            {
                if (mFreed.Count < mFreedMax)
                {
                    mFreed.Add(mDelivered);
                }
                else
                {
                    //Currently this should be impossible. 
                    //This warning indicates that we received a large number of frames at once
                    //before they finish processing (parallel processing enabled)
                    //If parallel processing is disabled this should be impossible as we never
                    //have more than 1 texture in processing at once.
                    Debug.LogWarning("Too many unused textures allocated. Releasing unused textures.");
                    Texture2D.Destroy(mDelivered);
                    mDelivered = null;
                }
            }
            mDelivered = mResults.texture;

            if(DEBUG_LOG)
            {
                //debug timing
                mDebugFpsCounter++;
                float timeSinceLast = Time.realtimeSinceStartup - mDebugFpsStartTime;
                if (timeSinceLast > mDebugFpsSampleTime)
                {
                    int fps = (int)Math.Round(mDebugFpsCounter / timeSinceLast);
                    Debug.Log("FrameProcessor for " + mResults.MetaData.ConnectionId + " FPS: " + fps
                        + " at " + mResults.MetaData.Width + "x" + mResults.MetaData.Height
                        + " Converter: " + mConverter.GetType().Name + "  mem: " + GetMem() + "MB");
                    mDebugFpsCounter = 0;
                    mDebugFpsStartTime = Time.realtimeSinceStartup;
                }
            }

            //Cleanup
            mArgs.Frame.Dispose();
            mArgs = null;
            var res = mResults;
            mResults = null;
            mIsBusy = false;
            return res;
        }

        /// <summary>
        /// Creates a valid converter for any given IFrame format.
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        private AFrameConverter PickConverter(IFrame frame)
        {
            //TODO: Same check is done in IsValid. We could use a single
            //array to check this and duplicate values that fit
            if (frame.Format == FramePixelFormat.I420p && frame is IDirectMemoryFrame)
            {
                //return new FrameConverter_I420p_to_R8();
                return new FrameConverter_I420p_to_RGBA32(FrameProcessor.PARALLEL_PROCESSING);
            }
            if (frame.Format == FramePixelFormat.Native && frame is TextureFrame)
            {
                //return new FrameConverter_I420p_to_R8();
                return new FrameConverter_WebGL_Native();
            }
            else
            {
                return new FrameConverter_ABGR_to_RGBA32();
            }
        }

        private Texture2D ReuseTexture()
        {
            Texture2D res = null;
            if (mFreed.Count > 0)
            {
                res = mFreed[0];
                mFreed.RemoveAt(0);
            }
            return res;
        }

        public void Cleanup()
        {

            if (this.mConverter != null)
            {
                this.mConverter.Dispose();
                this.mConverter = null;
            }
            foreach (var v in mFreed)
            {
                Texture2D.Destroy(v);
            }
            mFreed = new List<Texture2D>();
            if (this.mDelivered != null)
            {
                Texture2D.Destroy(this.mDelivered);
                this.mDelivered = null;
            }
        }
    }
}