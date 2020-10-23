﻿using LiteNetLib.Layers;
using System;
using System.Net;
using System.Security.Cryptography;
using LiteNetLib;

namespace LibSample
{
    /// <summary>
    /// Uses AES encryption in CBC mode. Make sure you handle your key properly.
    /// GCHandle.Alloc(key, GCHandleType.Pinned) to avoid your key being moved around different memory segments.
    /// ZeroMemory(gch.AddrOfPinnedObject(), key.Length); to erase it when you are done.
    /// Speed varies greatly depending on hardware encryption support. 
    /// Why encrypt: http://ithare.com/udp-for-games-security-encryption-and-ddos-protection/
    /// </summary>
    public struct AesEncryptLayer : IPacketLayer
    {
        public const int KeySize = 256;
        public const int BlockSize = 128;
        public const int KeySizeInBytes = KeySize / 8;
        public const int BlockSizeInBytes = BlockSize / 8;
        public const int ExtraHeaderSize = BlockSizeInBytes * 2; 
        
        private readonly AesCryptoServiceProvider _aes;

        private ICryptoTransform _encryptor;
        private ICryptoTransform _decryptor;

        private readonly byte[] _cipherBuffer; 
        private readonly byte[] _ivBuffer;

        /// <summary>
        /// Should be safe against eavesdropping, but is vulnerable to tampering
        /// Needs a HMAC on top of the encrypted content to be fully safe
        /// </summary>
        /// <param name="key"></param>
        public AesEncryptLayer(byte[] key)
        {
            if (key.Length != KeySizeInBytes) throw new NotSupportedException("EncryptLayer only supports keysize " + KeySize);

            _cipherBuffer= new byte[NetConstants.MaxPacketSize];//Max possible UDP packet size
            _ivBuffer = new byte[BlockSizeInBytes];

            //Switch this with AesGCM for better performance, requires .NET Core 3.0 or Standard 2.1
            _aes = new AesCryptoServiceProvider();
            _aes.KeySize = KeySize;
            _aes.BlockSize = BlockSize;
            _aes.Key = key;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.PKCS7;

            _encryptor = _aes.CreateEncryptor();
            _decryptor = _aes.CreateDecryptor();
        }

        public int ExtraPacketSize
        {
            get
            {
                return ExtraHeaderSize;
            }
        }

        public void ProcessInboundPacket(IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            //Can't copy directly to _aes.IV. It won't work for some reason.
            Buffer.BlockCopy(data, offset, _ivBuffer, 0, _ivBuffer.Length);
            //_aes.IV = ivBuffer;
            _decryptor = _aes.CreateDecryptor(_aes.Key, _ivBuffer);
            offset += BlockSizeInBytes;

            //int currentRead = ivBuffer.Length;
            //int currentWrite = 0;

            //TransformBlocks(_decryptor, data, length, ref currentRead, ref currentWrite);
            byte[] lastBytes = _decryptor.TransformFinalBlock(data, offset, length - offset);

            data = lastBytes;
            offset = 0;
            length = lastBytes.Length;
        }

        public void ProcessOutBoundPacket(IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            //Some Unity platforms may need these (and will be slower + generate garbage)
            if (!_encryptor.CanReuseTransform)
            {
                _aes.GenerateIV();
                _encryptor = _aes.CreateEncryptor();
            }

            //Copy IV in plaintext to output, this is standard practice
            _aes.IV.CopyTo(_cipherBuffer, 0);

            int currentRead = offset;
            int currentWrite = _aes.IV.Length;
            byte[] lastBytes = _encryptor.TransformFinalBlock(data, currentRead, length - offset);
            lastBytes.CopyTo(_cipherBuffer, currentWrite);
            //TransformBlocks(_encryptor, data, length, ref currentRead, ref currentWrite);

            data = _cipherBuffer;
            offset = 0;
            length = lastBytes.Length + BlockSizeInBytes;
        }

        private void TransformBlocks(ICryptoTransform transform, byte[] input, int inputLength, ref int currentRead, ref int currentWrite)
        {
            //This loop produces a invalid padding exception
            //I'm leaving it here as a start point in case others need support for 
            //Platforms wheere !transfom.CanTransformMultipleBlocks
            if (!transform.CanTransformMultipleBlocks)
            {
                while (inputLength - currentRead > BlockSizeInBytes)
                {
                    int encryptedCount = transform.TransformBlock(input, currentRead, BlockSizeInBytes, _cipherBuffer, currentWrite);
                    currentRead += encryptedCount;
                    currentWrite += encryptedCount;
                }
            }

            byte[] lastBytes = transform.TransformFinalBlock(input, currentRead, inputLength - currentRead);
            lastBytes.CopyTo(_cipherBuffer, currentWrite);
            currentWrite += lastBytes.Length;
        }
    }
}
