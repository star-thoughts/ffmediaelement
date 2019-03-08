﻿namespace Unosquare.FFME.Workers
{
    using Commands;
    using Decoding;
    using Primitives;
    using Shared;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Implement frame decoding worker logic
    /// </summary>
    /// <seealso cref="WorkerBase" />
    /// <seealso cref="IMediaWorker" />
    internal sealed class FrameDecodingWorker : ThreadWorkerBase, IMediaWorker, ILoggingSource
    {
        private readonly Action<IEnumerable<MediaType>, CancellationToken> SerialDecodeBlocks;
        private readonly Action<IEnumerable<MediaType>, CancellationToken> ParallelDecodeBlocks;

        /// <summary>
        /// The decoded frame count for a cycle
        /// </summary>
        private int DecodedFrameCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameDecodingWorker"/> class.
        /// </summary>
        /// <param name="mediaCore">The media core.</param>
        public FrameDecodingWorker(MediaEngine mediaCore)
            : base(nameof(FrameDecodingWorker), Constants.ThreadWorkerPeriod)
        {
            MediaCore = mediaCore;
            Commands = mediaCore.Commands;
            Container = mediaCore.Container;
            State = mediaCore.State;

            ParallelDecodeBlocks = (all, ct) =>
            {
                Parallel.ForEach(all, (t) =>
                    Interlocked.Add(ref DecodedFrameCount,
                    DecodeComponentBlocks(t, ct)));
            };

            SerialDecodeBlocks = (all, ct) =>
            {
                foreach (var t in Container.Components.MediaTypes)
                    DecodedFrameCount += DecodeComponentBlocks(t, ct);
            };
        }

        /// <inheritdoc />
        public MediaEngine MediaCore { get; }

        /// <inheritdoc />
        ILoggingHandler ILoggingSource.LoggingHandler => MediaCore;

        /// <summary>
        /// Gets the Media Engine's Command Manager.
        /// </summary>
        private CommandManager Commands { get; }

        /// <summary>
        /// Gets the Media Engine's Container.
        /// </summary>
        private MediaContainer Container { get; }

        /// <summary>
        /// Gets the Media Engine's State.
        /// </summary>
        private MediaEngineState State { get; }

        /// <summary>
        /// Gets a value indicating whether the decoder needs to wait for the reader to receive more packets.
        /// </summary>
        private bool NeedsMorePackets => MediaCore.ShouldReadMorePackets && !MediaCore.Container.Components.HasEnoughPackets;

        /// <inheritdoc />
        protected override void ExecuteCycleLogic(CancellationToken ct)
        {
            try
            {
                if (MediaCore.HasDecodingEnded || ct.IsCancellationRequested)
                    return;

                // We need to add blocks if the wall clock is over 75%
                // for each of the components so that we have some buffer.
                DecodedFrameCount = 0;
                if (Container.MediaOptions.UseParallelDecoding)
                    ParallelDecodeBlocks.Invoke(Container.Components.MediaTypes, ct);
                else
                    SerialDecodeBlocks.Invoke(Container.Components.MediaTypes, ct);
            }
            finally
            {
                // Provide updates to decoding stats
                State.UpdateDecodingBitRate(MediaCore.Blocks.Values.Sum(b => b.RangeBitRate));

                // Detect End of Decoding Scenarios
                // The Rendering will check for end of media when this condition is set.
                MediaCore.HasDecodingEnded = DetectHasDecodingEnded();
            }
        }

        /// <inheritdoc />
        protected override void OnCycleException(Exception ex) =>
            this.LogError(Aspects.DecodingWorker, "Worker Cycle exception thrown", ex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeComponentBlocks(MediaType t, CancellationToken ct)
        {
            // Capture a reference to the blocks and the current Range Percent
            const double rangePercentThreshold = 0.75d;

            var dropLateFrames = MediaCore.MediaOptions.DropLateFrames; // a reference to the flag
            var decoderBlocks = MediaCore.Blocks[t]; // the blocks reference
            var addedBlocks = 0; // the number of blocks that have been added
            var maxAddedBlocks = decoderBlocks.Capacity; // the max blocks to add for this cycle

            // We don't need the range percent if drop late frames is enabled
            var rangePercent = !dropLateFrames
                ? decoderBlocks.GetRangePercent(MediaCore.Clock.Position(t))
                : 0;

            while (addedBlocks < maxAddedBlocks)
            {
                if (dropLateFrames)
                {
                    // When drop late frames is enabled we want to decode as much as possible as
                    // long as the playback clock position is beyond the middle range of available block range
                    if (decoderBlocks.IsFull && MediaCore.Clock.Position(t) < decoderBlocks.RangeMidTime && MediaCore.Clock.Position(t) >= decoderBlocks.RangeStartTime)
                        break;
                }
                else
                {
                    // When drop late frames is disabled (the default behavior) we want to decode
                    // if the playback clock is about to get beyond the available block range.
                    if (decoderBlocks.IsFull && rangePercent <= rangePercentThreshold)
                        break;
                }

                // Try adding the next block. Stop decoding upon failure or cancellation
                if (ct.IsCancellationRequested || AddNextBlock(t) == false)
                    break;

                // At this point we notify that we have added the block
                addedBlocks++;

                // We don't need the range percent if drop late frames is enabled
                rangePercent = dropLateFrames ? 0 : decoderBlocks.GetRangePercent(MediaCore.Clock.Position(t));
            }

            return addedBlocks;
        }

        /// <summary>
        /// Tries to receive the next frame from the decoder by decoding queued
        /// Packets and converting the decoded frame into a Media Block which gets
        /// queued into the playback block buffer.
        /// </summary>
        /// <param name="t">The MediaType.</param>
        /// <returns>True if a block could be added. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AddNextBlock(MediaType t)
        {
            // Decode the frames
            var block = MediaCore.Blocks[t].Add(Container.Components[t].ReceiveNextFrame(), Container);
            return block != null;
        }

        /// <summary>
        /// Detects the end of media in the decoding worker.
        /// </summary>
        /// <returns>True if media docding has ended</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DetectHasDecodingEnded()
        {
            var main = Container.Components.MainMediaType;
            return DecodedFrameCount <= 0
                && CanReadMoreFramesOf(main) == false
                && MediaCore.Blocks[main].IndexOf(MediaCore.Clock.Position()) >= MediaCore.Blocks[main].Count - 1;
        }

        /// <summary>
        /// Gets a value indicating whether more frames can be decoded into blocks of the given type.
        /// </summary>
        /// <param name="t">The media type.</param>
        /// <returns>
        ///   <c>true</c> if more frames can be decoded; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanReadMoreFramesOf(MediaType t)
        {
            return
                Container.Components[t].BufferLength > 0 ||
                Container.Components[t].HasPacketsInCodec ||
                MediaCore.ShouldReadMorePackets;
        }
    }
}
