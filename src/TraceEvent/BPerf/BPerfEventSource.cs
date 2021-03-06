﻿//     Copyright (c) Microsoft Corporation.  All rights reserved.
// This file is best viewed using outline mode (Ctrl-M Ctrl-O)
//
// This program uses code hyperlinks available as part of the HyperAddin Visual Studio plug-in.
// It is available from http://www.codeplex.com/hyperAddin 
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Microsoft.Diagnostics.Tracing
{
    /// <summary>
    /// BPerf Trace Log (BTL) are files generated by the CPU Samples Collector tool in https://github.com/Microsoft/BPerf
    /// The layout of the file is as follows -->
    /// 
    /// Format:
    /// 4 byte integer describing compressed size
    /// 4 byte integer describing uncompressed size
    /// byte[compressed size]
    /// 
    /// The byte array is a list of EVENT_RECORDs. Each Event_RECORD is aligned to 16-bytes.
    /// 
    /// The EVENT_RECORD is laid out as a memory dump of the structure in memory. All pointers from
    /// the structure are laid out successively in front of the EVENT_RECORD.
    /// 
    /// The compression mechanism is using the NTDLL.RtlDecompressBufferEx Express Huffman procedure.
    /// </summary>
    public sealed class BPerfEventSource : TraceEventDispatcher, IDisposable
    {
        private const int OffsetToExtendedData = 88; // offsetof(TraceEventNativeMethods.EVENT_RECORD*, ExtendedData);

        private const int OffsetToUTCOffsetMinutes = 64; // offsetof(FAKE_TRACE_LOGFILE_HEADER, OrigHdr.TimeZone.Bias);

        private const ushort CompressionFormatXpressHuff = 4;

        private const int BufferSize = 1024 * 1024 * 2;

        private const int ReadAheadBufferSize = 1024 * 1024 * 1;

        private static readonly Guid EventTraceGuid = new Guid("{68fdd900-4a3e-11d1-84f4-0000f80464e3}");

        private readonly string btlFilePath;

        private readonly long fileOffset;

        private readonly byte[] workspace;

        private readonly byte[] uncompressedBuffer;

        private readonly byte[] compressedBuffer;

        private readonly Dictionary<int, string> processNameForID = new Dictionary<int, string>();

        private int eventsLost;

        public BPerfEventSource(string btlFilePath)
            : this(btlFilePath, 0)
        {
        }

        /// <summary>
        /// This constructor is used when the consumer has an offset within the BTL file that it would like to seek to.
        /// </summary>
        public BPerfEventSource(string btlFilePath, long fileOffset)
            : this(btlFilePath, fileOffset, new byte[BufferSize], new byte[BufferSize], new byte[BufferSize])
        {
        }

        /// <summary>
        /// This constructor is used when the consumer is supplying the buffers for reasons like buffer pooling.
        /// </summary>
        public BPerfEventSource(string btlFilePath, long fileOffset, byte[] workspace, byte[] uncompressedBuffer, byte[] compressedBuffer)
        {
            this.btlFilePath = btlFilePath;
            this.fileOffset = fileOffset;
            this.workspace = workspace;
            this.uncompressedBuffer = uncompressedBuffer;
            this.compressedBuffer = compressedBuffer;

            this.ProcessInner();
        }

        public override int EventsLost => this.eventsLost;

        public override long Size => File.Exists(this.btlFilePath) ? new FileInfo(this.btlFilePath).Length : 0;

        public override bool Process()
        {
            this.ProcessInner();
            return true;
        }

        internal override string ProcessName(int processID, long time100ns)
        {
            if (!this.processNameForID.TryGetValue(processID, out var ret))
            {
                ret = string.Empty;
            }

            return ret;
        }

        private void ProcessInner()
        {
            using (var fs = new FileStream(this.btlFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long remainingFileSize = (fs.Length - fileOffset) & ~(16 - 1); // align down to 16 byte boundary
                int offset = 0;

                while (remainingFileSize > 0)
                {
                    bool exitCondition = false;
                    int bytesToRead = (int)Math.Min(remainingFileSize, ReadAheadBufferSize);
                    int savedOffset = offset;
                    int remainder = 0;

                    if (bytesToRead < ReadAheadBufferSize)
                    {
                        remainder = offset;
                        offset = 0;
                    }

                    int realReadBytes = bytesToRead - offset;
                    while (bytesToRead > offset)
                    {
                        int bytesRead = fs.Read(this.compressedBuffer, savedOffset, bytesToRead - offset);

                        // this can happen when we open a not completely written file
                        // so we don't want to end up in an infinte loop so check if we're making forward progress
                        // and if not break.
                        if (bytesRead == 0)
                        {
                            exitCondition = true;
                            break;
                        }

                        offset += bytesRead;
                    }

                    int retval = this.ProcessBTLInner(bytesToRead + remainder);

                    if (exitCondition || retval == -1) // retval == -1 means initialization loop, so bail.
                    {
                        break;
                    }

                    Array.Copy(this.compressedBuffer, retval, this.compressedBuffer, 0, bytesToRead + remainder - retval);

                    offset = bytesToRead - retval;
                    remainingFileSize -= realReadBytes;
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe bool Initialize(TraceEventNativeMethods.EVENT_RECORD* eventRecord)
        {
            if (eventRecord->EventHeader.ProviderId == EventTraceGuid)
            {
                var logfile = (FAKE_TRACE_LOGFILE_HEADER*)eventRecord->UserData;
                this.pointerSize = (int)logfile->PointerSize;
                this._QPCFreq = logfile->PerfFreq;
                this.osVersion = new Version((byte)logfile->Version, (byte)(logfile->Version >> 8));
                this.cpuSpeedMHz = (int)logfile->CpuSpeedInMHz;
                this.numberOfProcessors = (int)logfile->NumberOfProcessors;
                this.utcOffsetMinutes = logfile->Bias;
                this._syncTimeUTC = DateTime.FromFileTimeUtc(logfile->StartTime);
                this._syncTimeQPC = eventRecord->EventHeader.TimeStamp;
                this.sessionStartTimeQPC = eventRecord->EventHeader.TimeStamp;
                this.sessionEndTimeQPC = eventRecord->EventHeader.TimeStamp;
                this.eventsLost = (int)logfile->EventsLost;

                var kernelParser = new KernelTraceEventParser(this, KernelTraceEventParser.ParserTrackingOptions.None);

                kernelParser.EventTraceHeader += delegate (EventTraceHeaderTraceData data)
                {
                    Marshal.WriteInt32(data.userData, OffsetToUTCOffsetMinutes, this.utcOffsetMinutes.Value); // Since this is a FakeTraceHeader
                };

                kernelParser.ProcessStartGroup += delegate (ProcessTraceData data)
                {
                    string path = data.KernelImageFileName;
                    int startIdx = path.LastIndexOf('\\');
                    if (0 <= startIdx)
                    {
                        startIdx++;
                    }
                    else
                    {
                        startIdx = 0;
                    }

                    int endIdx = path.LastIndexOf('.');
                    if (endIdx <= startIdx)
                    {
                        endIdx = path.Length;
                    }

                    this.processNameForID[data.ProcessID] = path.Substring(startIdx, endIdx - startIdx);
                };

                kernelParser.ProcessEndGroup += delegate (ProcessTraceData data)
                {
                    this.processNameForID.Remove(data.ProcessID);
                };

                return true;
            }

            return false;
        }

        private unsafe int ProcessBTLInner(int eof)
        {
            int offset = 0;
            while (offset + 8 < eof)
            {
                int compressedBufferSize = BitConverter.ToInt32(this.compressedBuffer, offset);
                offset += 4;

                int uncompressedBufferSize = BitConverter.ToInt32(this.compressedBuffer, offset);
                offset += 4;

                if (offset + compressedBufferSize > eof)
                {
                    return offset - 8; // the two ints compressedBufferSize & uncompressedBufferSize
                }

                fixed (byte* uncompressedBufferPtr = &this.uncompressedBuffer[0])
                fixed (byte* compressedBufferPtr = &this.compressedBuffer[0])
                fixed (byte* workspacePtr = &this.workspace[0])
                {
                    if (RtlDecompressBufferEx(CompressionFormatXpressHuff, uncompressedBufferPtr, uncompressedBufferSize, compressedBufferPtr + offset, compressedBufferSize, out var finalUncompressedSize, workspacePtr) != 0)
                    {
                        throw new Exception("Decompression failed");
                    }

                    int bufferOffset = 0;
                    while (bufferOffset < finalUncompressedSize)
                    {
                        var eventRecord = (TraceEventNativeMethods.EVENT_RECORD*)(uncompressedBufferPtr + bufferOffset);

                        bufferOffset += OffsetToExtendedData;
                        bufferOffset += 24; // store space for 3 8-byte pointers regardless of arch

                        eventRecord->UserData = new IntPtr(uncompressedBufferPtr + bufferOffset);

                        bufferOffset += eventRecord->UserDataLength;
                        bufferOffset = AlignUp(bufferOffset, 8);

                        eventRecord->ExtendedData = eventRecord->ExtendedDataCount > 0 ? (TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM*)(uncompressedBufferPtr + bufferOffset) : (TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM*)0;

                        bufferOffset += sizeof(TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM) * eventRecord->ExtendedDataCount;

                        for (ushort i = 0; i < eventRecord->ExtendedDataCount; ++i)
                        {
                            eventRecord->ExtendedData[i].DataPtr = (ulong)(uncompressedBufferPtr + bufferOffset);

                            bufferOffset += eventRecord->ExtendedData[i].DataSize;
                            bufferOffset = AlignUp(bufferOffset, 8);
                        }

                        bufferOffset = AlignUp(bufferOffset, 16);

                        if (this.sessionStartTimeQPC == 0)
                        {
                            if (!this.Initialize(eventRecord))
                            {
                                continue;
                            }
                            else
                            {
                                return -1; // -1 means initialization succeeded.
                            }
                        }

                        // BPerf puts 65535 for Classic Events, TraceEvent fires an assert for that case.
                        if ((eventRecord->EventHeader.Flags & TraceEventNativeMethods.EVENT_HEADER_FLAG_CLASSIC_HEADER) != 0)
                        {
                            eventRecord->EventHeader.Id = 0;
                        }

                        var traceEvent = this.Lookup(eventRecord);
                        traceEvent.DebugValidate();

                        if (traceEvent.NeedsFixup)
                        {
                            traceEvent.FixupData();
                        }

                        this.Dispatch(traceEvent);
                        this.sessionEndTimeQPC = eventRecord->EventHeader.TimeStamp;
                    }

                    offset += compressedBufferSize;
                    offset = AlignUp(offset, 4);
                }
            }

            return offset;
        }

        private static int AlignUp(int num, int align)
        {
            return (num + (align - 1)) & ~(align - 1);
        }

        [DllImport("ntdll.dll")]
        private static extern unsafe uint RtlDecompressBufferEx(ushort compressionFormat, byte* uncompressedBuffer, int uncompressedBufferSize, byte* compressedBuffer, int compressedBufferSize, out int finalUncompressedSize, byte* workSpace);

        [StructLayout(LayoutKind.Explicit)]
        private struct FAKE_TRACE_LOGFILE_HEADER
        {
            [FieldOffset(0x4)]
            public uint Version;

            [FieldOffset(0xC)]
            public uint NumberOfProcessors;

            [FieldOffset(0x2C)]
            public uint PointerSize;

            [FieldOffset(0x30)]
            public uint EventsLost;

            [FieldOffset(0x34)]
            public uint CpuSpeedInMHz;

            [FieldOffset(0x44)]
            public int Bias;

            [FieldOffset(0xF8)]
            public long PerfFreq;

            [FieldOffset(0x0100)]
            public long StartTime;
        }
    }
}