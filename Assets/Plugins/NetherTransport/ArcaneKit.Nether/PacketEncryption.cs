using System;

namespace ArcaneKit.Nether
{
    public class PacketEncryption : IDisposable
    {
        public const int IvSize = 12;
        public const int TagSize = 16;
        // private readonly byte[] iv = new byte[IvSize];
        // private readonly byte[] tag = new byte[TagSize];
        // private readonly byte[] key;
        // private readonly AesGcm aes;

        public PacketEncryption(string secret)
        {
            // key = Convert.FromBase64String(secret);
            // aes = new AesGcm(key);
        }

        public int Encrypt(Span<byte> result, Span<byte> rawData)
        {
            // RandomNumberGenerator.Fill(iv);
            //
            // aes.Encrypt(iv, rawData, result.Slice(0, rawData.Length), tag);
            //
            // int totalLength = iv.Length + tag.Length + rawData.Length;
            // if (result.Length < totalLength)
            //     throw new ArgumentException("Result span too small.");
            //
            // Span<byte> ciphertext = result.Slice(iv.Length + tag.Length, rawData.Length);
            // aes.Encrypt(iv, rawData, ciphertext, tag);
            //
            // iv.CopyTo(result.Slice(0, iv.Length));
            // tag.CopyTo(result.Slice(iv.Length, tag.Length));
            //
            // return totalLength;
            rawData.CopyTo(result);
            return rawData.Length;
        }

        public int Decrypt(ReadOnlySpan<byte> encryptedData, Span<byte> decryptedOutput)
        {
            // if (encryptedData.Length < IvSize + TagSize)
            //     throw new ArgumentException("Encrypted data too short.");
            //
            // ReadOnlySpan<byte> iv = encryptedData.Slice(0, IvSize);
            // ReadOnlySpan<byte> tag = encryptedData.Slice(IvSize, TagSize);
            // ReadOnlySpan<byte> ciphertext = encryptedData.Slice(IvSize + TagSize, encryptedData.Length - TagSize - IvSize);
            //
            // if (decryptedOutput.Length < ciphertext.Length)
            //     throw new ArgumentException("Decrypted output span too small.");
            //
            // aes.Decrypt(iv, ciphertext, tag, decryptedOutput[..ciphertext.Length]);
            // return ciphertext.Length;
            encryptedData.CopyTo(decryptedOutput);
            return encryptedData.Length;
        }


        public void Dispose()
        {
            // aes.Dispose();
        }
    }
}