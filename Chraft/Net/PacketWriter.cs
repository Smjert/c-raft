﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Chraft.Net.Packets;

namespace Chraft.Net
{
    public class PacketWriter
    {
        private static Stack<PacketWriter> _Pool = new Stack<PacketWriter>();

        public StreamRole Role { get; private set; }

        private int _Capacity;
        public int Capacity
        {
            get { return _Capacity; }
            set { _Capacity = value; }
        }

        private MemoryStream _Stream;
        public MemoryStream UnderlyingStream
        {
            get { return _Stream; }
            set { _Stream = value; }
        }

        private Queue<byte[]> _Strings;
        public Queue<byte[]> Strings
        {
            get { return _Strings; }
            set { _Strings = value; }
        }

        public PacketWriter(int capacity, StreamRole role)
        {
            _Stream = new MemoryStream(capacity);
            _Capacity = capacity;
            Role = role;
        }

        public static PacketWriter CreateInstance(int capacity, StreamRole role, Queue<byte[]> strings)
        {
            PacketWriter pw = CreateInstance(capacity, role);
            pw.Strings = strings;
            return pw;
        }

        public static PacketWriter CreateInstance(StreamRole role)
        {
            return CreateInstance(32, role);
        }

        public static PacketWriter CreateInstance(int capacity, StreamRole role)
        {
            PacketWriter pw = null;

            if (_Pool.Count > 0)
            {
                pw = _Pool.Pop();

                if (pw != null)
                {
                    pw._Capacity = capacity;
                    pw._Stream.SetLength(0);
                }
            }

            if (pw == null)
                pw = new PacketWriter(capacity, role);

            return pw;
        }

        public static void ReleaseInstance(PacketWriter pw)
        {
            if (!_Pool.Contains(pw))
                _Pool.Push(pw);
            else
            {
                try
                {
                    using (StreamWriter op = new StreamWriter("neterr.log"))
                    {
                        op.WriteLine("{0}\tInstance pool contains writer", DateTime.Now);
                    }
                }
                catch
                {
                    Console.WriteLine("net error");
                }
            }
        }

        public void Write(byte data)
        {
            _Stream.WriteByte(data);
        }

        public void WriteByte(byte data)
        {
            _Stream.WriteByte(data);
        }

        public void Write(sbyte data)
        {
            Write(unchecked((byte)data));
        }

        public void Write(short data)
        {
            Write(unchecked((byte)(data >> 8)));
            Write(unchecked((byte)data));
        }

        public void Write(int data)
        {
            Write(unchecked((byte)(data >> 24)));
            Write(unchecked((byte)(data >> 16)));
            Write(unchecked((byte)(data >> 8)));
            Write(unchecked((byte)data));
        }

        public void Write(long data)
        {
            Write(unchecked((byte)(data >> 56)));
            Write(unchecked((byte)(data >> 48)));
            Write(unchecked((byte)(data >> 40)));
            Write(unchecked((byte)(data >> 32)));
            Write(unchecked((byte)(data >> 24)));
            Write(unchecked((byte)(data >> 16)));
            Write(unchecked((byte)(data >> 8)));
            Write(unchecked((byte)data));
        }

        public unsafe void Write(float data)
        {
            Write(*(int*)&data);
        }

        public unsafe void Write(double data)
        {
            Write(*(long*)&data);
        }

        public void Write(string data)
        {
            byte[] b;
            if (_Strings != null)
                b = _Strings.Dequeue();
            else
                b = ASCIIEncoding.BigEndianUnicode.GetBytes(data);

            Write((short)data.Length);
            Write(b, 0, b.Length);
        }

        public void Write8(string data)
        {
            byte[] b = ASCIIEncoding.UTF8.GetBytes(data);
            Write((short)b.Length);
            Write(b, 0, b.Length);
        }

        public void Write(bool data)
        {
            Write((byte)(data ? 1 : 0));
        }

        public void WritePacket(Packet packet)
        {
            Write((byte)packet.GetPacketType());
            //packet.WriteFlush(this);
        }

        public void Write(MetaData Data)
        {
            Data.Write(this);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            _Stream.Write(buffer, offset, count);
        }

        public void WriteDoublePacked(double d)
        {
            Write((int)(d * 32.0));
        }
        
    }
}
