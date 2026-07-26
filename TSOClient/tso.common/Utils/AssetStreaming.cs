using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Common.Utils
{
    public enum AssetStreamingMode
    {
        None = 0,
        Lot = 1,
    }

    public static class AssetStreaming
    {
        private static object _StreamCallbacksLock = new object();
        private static Queue<Callback> _StreamUpdateCallbacks = new Queue<Callback>();
        private static Queue<Callback> _StreamUpdateCallbacksSwap = new Queue<Callback>();

        // volatile: written by the render thread (BeginStreaming/EndStreaming) and read by loader threads.
        // ARM's memory model is weakly ordered - unlike x86 - so on Apple Silicon a worker could otherwise
        // keep observing a stale value after EndStreaming and pick the wrong branch in LoadTexture.
        private static volatile AssetStreamingMode _LoadingType = AssetStreamingMode.None;
        private static int _LoadingCount;

        private static int _LoadingRequests;
        private static int _LoadingComplete;

        public static AssetStreamingMode LoadingType => _LoadingType;

        public static void DigestStreamUpdate()
        {
            Queue<Callback> _callbacks;

            lock (_StreamCallbacksLock)
            {
                // Swap the active callbacks queue with the second one, so we can
                // process entries without fear of more being added.

                _callbacks = _StreamUpdateCallbacks;
                _StreamUpdateCallbacks = _StreamUpdateCallbacksSwap;
                _StreamUpdateCallbacksSwap = _callbacks;
            }

            // These callbacks have a frametime budget. If it's exceeded, the callbacks are pushed onto the next frame.
            float frameAllowance = 0.002f;
            float budgetSeconds = Math.Max(1f / FSOEnvironment.RefreshRate - frameAllowance, 0.005f);
            long budgetTicks = (long)(Stopwatch.Frequency * budgetSeconds);

            long startTime = Stopwatch.GetTimestamp();

            while (_callbacks.Count > 0)
            {
                _callbacks.Dequeue()();

                long now = Stopwatch.GetTimestamp();

                if ((now - startTime) > budgetTicks)
                {
                    break;
                }
            }

            if (_callbacks.Count > 0)
            {
                lock (_StreamCallbacksLock)
                {
                    // Push remaining callbacks onto the next frame.

                    while (_callbacks.Count > 0)
                    {
                        _StreamUpdateCallbacks.Enqueue(_callbacks.Dequeue());
                    }
                }
            }
        }

        public static void InStreamUpdate(Callback callback)
        {
            lock (_StreamCallbacksLock)
            {
                _StreamUpdateCallbacks.Enqueue(callback);
            }
        }

        /// <summary>
        /// Load a texture with support for multithreading during loading screens.
        /// The texture should already be created with the correct width and height.
        /// The data provider is called from a background thread when a loading state is active,
        /// otherwise it's done in the current thread.
        /// </summary>
        /// <typeparam name="T">Texture data type</typeparam>
        /// <param name="tex">Texture to put data into</param>
        /// <param name="type">The type of loading required for this texture load to multithread</param>
        /// <param name="dataProvider">Texture data generator</param>
        public static void LoadTexture<T>(Texture2D tex, AssetStreamingMode type, Func<TextureData<T>[]> dataProvider) where T : struct
        {
            if (_LoadingType >= type)
            {
                // Async load. Try source the data on a task, then set it on the game thread.
                AddLoadingResource();

                Task.Run(dataProvider).ContinueWith((taskResult) =>
                {
                    var data = taskResult.Result;
                    InStreamUpdate(() =>
                    {
                        TextureUtils.UploadTexData(tex, data);

                        RemoveLoadingResource();
                    });
                });
            }
            else if (!GameThread.IsInGameThread())
            {
                // Streaming is off, but we are NOT on the render thread - so the synchronous path below
                // would upload texture data from whatever thread we happen to be on. An OpenGL context is
                // current on exactly one thread, so on macOS that call lands with no context bound and
                // segfaults inside glGetIntegerv (no managed exception - the CLR never sees it).
                //
                // This is reachable during lot entry: VMStateSyncCmd runs the whole lot load on a
                // ThreadPool worker, while EndStreaming() flips _LoadingType back to None from the render
                // thread (UILayer.AddScreen) - so a texture requested late in the load takes this branch.
                // Decode here (that part is pure CPU work and is why we're on a worker at all) and hand
                // only the upload to the render thread.
                AddLoadingResource();
                var data = dataProvider();
                InStreamUpdate(() =>
                {
                    TextureUtils.UploadTexData(tex, data);
                    RemoveLoadingResource();
                });
            }
            else
            {
                var data = dataProvider();

                TextureUtils.UploadTexData(tex, data);
            }
        }

        public static void BeginStreaming(AssetStreamingMode type)
        {
            _LoadingType = type;
        }

        /// <summary>
        /// Ends the multithreaded loading period.
        /// Returns true when there are no pending loads.
        /// </summary>
        /// <returns></returns>
        public static bool EndStreaming()
        {
            _LoadingType = AssetStreamingMode.None;

            return Volatile.Read(ref _LoadingCount) == 0;
        }

        public static void AddLoadingResource()
        {
            Interlocked.Increment(ref _LoadingRequests);
            Interlocked.Increment(ref _LoadingCount);
        }

        public static void RemoveLoadingResource()
        {
            Interlocked.Increment(ref _LoadingComplete);
            Interlocked.Decrement(ref _LoadingCount);
        }

        public static int GetCheckpoint()
        {
            // Note: doesn't entirely work as intended right now.
            // Mesh reading tasks can dispatch other texture reading tasks that aren't counted when the checkpoint was taken.

            return Volatile.Read(ref _LoadingRequests);
        }

        public static bool IsCheckpointMet(int checkpoint)
        {
            int diff = Volatile.Read(ref _LoadingComplete) - checkpoint;

            return diff >= 0;
        }
    }
}
