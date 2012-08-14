﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace Chraft.Net
{
    public static class PacketCryptography
    {
        /* This is hardcoded since it won't change. 
           It's 1 byte Sequence Tag, 1 byte Sequence Length, 1 byte Oid Tag, 1 byte Oid Length, 9 bytes Oid, 1 byte Null Tag, 1 byte Null */
        private static byte[] algorithmId = new byte[] { 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00 };

        private static RSACryptoServiceProvider _provider = new RSACryptoServiceProvider(1024);
        public static RSAParameters GeneratePublicKey()
        {
            return _provider.ExportParameters(true);
        }

        public static RSAParameters GeneratePrivateKey()
        {
            return _provider.ExportParameters(true);
        }

        public static byte[] Decrypt(byte[] toDecrypt)
        {
            return _provider.Decrypt(toDecrypt, false);
        }

        public static RijndaelManaged GenerateAES(byte[] key)
        {
            RijndaelManaged cipher = new RijndaelManaged();
            cipher.Mode = CipherMode.CFB;
            cipher.Padding = PaddingMode.None;
            cipher.KeySize = 128;
            cipher.FeedbackSize = 8;
            cipher.Key = key;
            cipher.IV = key;

            return cipher;
        }

        public static byte[] GetRandomToken()
        {
            byte[] token = new byte[4];
            RNGCryptoServiceProvider provider = new RNGCryptoServiceProvider();
            provider.GetBytes(token);

            return token;
        }

        public static byte[] PublicKeyToAsn1(RSAParameters parameters)
        {
            // Oid - Tag: 0x06 - Length: 0x09 - Octets: 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01
            // AlgorithmId - Tag: 0x30 - Length: 0x0D - Octets: 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00

            // mod - Tag: 0x02 - Length: ? - Octets: 0x00?, parameters.Modulus
            // exp - Tag: 0x02 - Length: ? - Octets: 0x00?, parameters.Exponent            
            byte[] mod = CreateIntegerPos(parameters.Modulus);
            byte[] exp = CreateIntegerPos(parameters.Exponent);

            // Sequence(mod + exp) - Tag: 0x30 - Length: ? - Octets: mod + exp
            // Key - Tag: 0x03 - Length: ? - Octets: 0x00, Sequence(mod + exp)
            // PublicKey - Tag: 0x30 - Length: ? - Octets: AlgorithmId + Key
            // AsnMessage - PublicKey

            int sequenceOctetsLength = mod.Length + exp.Length;
            byte[] sequenceLengthArray = LengthToByteArray(sequenceOctetsLength);

            int keyOctetsLength = sequenceLengthArray.Length + sequenceOctetsLength + 2;
            byte[] keyLengthArray = LengthToByteArray(keyOctetsLength);

            int publicKeyOctetsLength = keyOctetsLength + keyLengthArray.Length + algorithmId.Length + 1;
            byte[] publicKeyLengthArray = LengthToByteArray(publicKeyOctetsLength);

            int messageLength = publicKeyOctetsLength + publicKeyLengthArray.Length + 1;

            byte[] message = new byte[messageLength];
            int index = 0;

            message[index++] = 0x30;
            
            Buffer.BlockCopy(publicKeyLengthArray, 0, message, index, publicKeyLengthArray.Length);
            index += publicKeyLengthArray.Length;

            Buffer.BlockCopy(algorithmId, 0, message, index, algorithmId.Length);

            index += algorithmId.Length;

            message[index++] = 0x03;

            Buffer.BlockCopy(keyLengthArray, 0, message, index, keyLengthArray.Length);
            index += keyLengthArray.Length;

            message[index++] = 0x00;
            message[index++] = 0x30;

            Buffer.BlockCopy(sequenceLengthArray, 0, message, index, sequenceLengthArray.Length);
            index += sequenceLengthArray.Length;

            Buffer.BlockCopy(mod, 0, message, index, mod.Length);
            index += mod.Length;
            Buffer.BlockCopy(exp, 0, message, index, exp.Length);

            //Console.WriteLine(BitConverter.ToString(message));
            return message;
        }
        
        private static byte[] LengthToByteArray(int octetsLength)
        {
            byte[] length = null;

            // Length: 0 <= l < 0x80
            if (octetsLength < 0x80)
            {
                length = new byte[1];
                length[0] = (byte)octetsLength;
            }
            // 0x80 < length <= 0xFF
            else if (octetsLength <= 0xFF)
            {
                length = new byte[2];
                length[0] = 0x81;
                length[1] = (byte)((octetsLength & 0xFF));
            }

            //
            // We should almost never see these...
            //

            // 0xFF < length <= 0xFFFF
            else if (octetsLength <= 0xFFFF)
            {
                length = new byte[3];
                length[0] = 0x82;
                length[1] = (byte)((octetsLength & 0xFF00) >> 8);
                length[2] = (byte)((octetsLength & 0xFF));
            }

            // 0xFFFF < length <= 0xFFFFFF
            else if (octetsLength <= 0xFFFFFF)
            {
                length = new byte[4];
                length[0] = 0x83;
                length[1] = (byte)((octetsLength & 0xFF0000) >> 16);
                length[2] = (byte)((octetsLength & 0xFF00) >> 8);
                length[3] = (byte)((octetsLength & 0xFF));
            }
            // 0xFFFFFF < length <= 0xFFFFFFFF
            else
            {
                length = new byte[5];
                length[0] = 0x84;
                length[1] = (byte)((octetsLength & 0xFF000000) >> 24);
                length[2] = (byte)((octetsLength & 0xFF0000) >> 16);
                length[3] = (byte)((octetsLength & 0xFF00) >> 8);
                length[4] = (byte)((octetsLength & 0xFF));
            }

            return length;
        }

        private static byte[] CreateIntegerPos(byte[] value)
        {
            byte[] newInt;
            
            if (value[0] > 0x7F)
            {
                // Integer length + Positive byte
                byte[] length = LengthToByteArray(value.Length + 1);
                int index = 1;
                // Tag + Length + Positive byte + Value
                newInt = new byte[value.Length + 2 + length.Length];
                // Int Tag
                newInt[0] = 0x02;
                // Int Length
                for (int i = 0; i < length.Length; ++i)
                    newInt[index++] = length[i];               
                
                // Makes the number positive
                newInt[index++] = 0x00;
                Buffer.BlockCopy(value, 0, newInt, index, value.Length);
            }
            else
            {
                byte[] length = LengthToByteArray(value.Length);
                int index = 1;

                // Tag + Length + Value
                newInt = new byte[value.Length + 1 + length.Length];
                // Int Tag
                newInt[0] = 0x02;
                // Int Length
                for (int i = 0; i < length.Length; ++i)
                    newInt[index++] = length[i];
                
                Buffer.BlockCopy(value, 0, newInt, index, value.Length);
            }

            return newInt;
        }
    }
}
