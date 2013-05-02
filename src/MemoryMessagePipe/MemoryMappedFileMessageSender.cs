﻿using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace MemoryMessagePipe
{
    public class MemoryMappedFileMessageSender : IDisposable
    {
        private static readonly int SizeOfFile = Environment.SystemPageSize;
        private const int SizeOfInt32 = sizeof(int);
        private const int SizeOfBool = sizeof(bool);
        private static readonly int SizeOfStream = SizeOfFile - SizeOfInt32 - SizeOfBool - SizeOfBool;

        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _bytesWrittenAccessor;
        private readonly MemoryMappedViewAccessor _messageCompletedAccessor;
        private readonly MemoryMappedViewStream _stream;
        private readonly EventWaitHandle _messageSendingEvent;
        private readonly EventWaitHandle _messageReadEvent;
        private readonly EventWaitHandle _bytesWrittenEvent;
        private readonly EventWaitHandle _bytesReadEvent;

        public MemoryMappedFileMessageSender(string name)
        {
            _file = MemoryMappedFile.CreateOrOpen(name, SizeOfFile);
            _bytesWrittenAccessor = _file.CreateViewAccessor(0, SizeOfInt32);
            _messageCompletedAccessor = _file.CreateViewAccessor(SizeOfInt32, SizeOfBool);
            _stream = _file.CreateViewStream(SizeOfInt32 + SizeOfBool + SizeOfBool, SizeOfStream);
            _messageSendingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_MessageSending");
            _messageReadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_MessageRead");
            _bytesWrittenEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_BytesWritten");
            _bytesReadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_BytesRead");
        }

        public void Dispose()
        {
            _bytesWrittenAccessor.Dispose();
            _messageCompletedAccessor.Dispose();
            _stream.Dispose();
            _bytesWrittenEvent.Dispose();
            _bytesReadEvent.Dispose();
        }

        public void SendMessage(Action<Stream> action)
        {
            _messageSendingEvent.Set();

            using (var stream = new MemoryMappedOutputStream(_bytesWrittenAccessor, _messageCompletedAccessor, _stream, _bytesWrittenEvent, _bytesReadEvent))
            {
                action(stream);
            }

            _messageReadEvent.WaitOne();
        }

        private class MemoryMappedOutputStream : Stream
        {
            private readonly MemoryMappedViewAccessor _bytesWrittenAccessor;
            private readonly MemoryMappedViewAccessor _messageCompletedAccessor;
            private readonly MemoryMappedViewStream _stream;
            private readonly EventWaitHandle _bytesWrittenEvent;
            private readonly EventWaitHandle _bytesReadEvent;

            public MemoryMappedOutputStream(MemoryMappedViewAccessor bytesWrittenAccessor,
                                            MemoryMappedViewAccessor messageCompletedAccessor,
                                            MemoryMappedViewStream stream,
                                            EventWaitHandle bytesWrittenEvent,
                                            EventWaitHandle bytesReadEvent)
            {
                _bytesWrittenAccessor = bytesWrittenAccessor;
                _messageCompletedAccessor = messageCompletedAccessor;
                _stream = stream;
                _bytesWrittenEvent = bytesWrittenEvent;
                _bytesReadEvent = bytesReadEvent;
            }

            private int _bytesWritten;

            public override void Close()
            {
                _messageCompletedAccessor.Write(0, true);

                if (_bytesWritten > 0)
                {
                    _bytesWrittenAccessor.Write(0, _bytesWritten);
                }

                _bytesWrittenEvent.Set();
                _bytesReadEvent.WaitOne();

                _bytesWrittenAccessor.Write(0, 0);
                _messageCompletedAccessor.Write(0, false);
                _stream.Seek(0, SeekOrigin.Begin);

                base.Close();
            }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null)
                    throw new ArgumentNullException("buffer", "Buffer cannot be null.");
                if (offset < 0)
                    throw new ArgumentOutOfRangeException("offset", "Non-negative number required.");
                if (count < 0)
                    throw new ArgumentOutOfRangeException("count", "Non-negative number required.");
                if (buffer.Length - offset < count)
                    throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");

                var bytesRemainingInStream = SizeOfStream - _bytesWritten;

                if (bytesRemainingInStream < count)
                {
                    _stream.Write(buffer, offset, bytesRemainingInStream);
                    _bytesWrittenAccessor.Write(0, SizeOfStream);

                    _bytesWrittenEvent.Set();
                    _bytesReadEvent.WaitOne();

                    _stream.Seek(0, SeekOrigin.Begin);
                    var bytesRemainingToWrite = count - bytesRemainingInStream;
                    _stream.Write(buffer, offset + bytesRemainingInStream, bytesRemainingToWrite);
                    _bytesWritten = bytesRemainingToWrite;
                }
                else
                {
                    _stream.Write(buffer, offset, count);
                    _bytesWritten += count;
                }
            }

            public override bool CanRead
            {
                get { return false; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return true; }
            }

            public override long Length
            {
                get { throw new NotSupportedException(); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }
        }
    }
}