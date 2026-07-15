// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Text;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Computes the Orbis (PS4/PS5) symbol NID from a plain export name. Module
/// dynamic-symbol tables are keyed by NID rather than by name, so name-based
/// lookups (notably <c>sceKernelDlsym</c> on a game-private symbol) must hash
/// the name first.
/// </summary>
/// <remarks>
/// NID = base64( reverse( SHA1(name || suffix)[0..8] ) )[0..11], where the
/// 16-byte suffix is the fixed Sony salt and the standard base64 alphabet is
/// used. The 8-byte digest prefix is byte-reversed because it is the
/// little-endian encoding of the 64-bit hash. Verified against known system
/// pairs (scePthreadExit -> 3kg7rT0NQIs, sceKernelLoadStartModule -> wzvqT4UqKX8).
/// </remarks>
public static class OrbisNid
{
    private static readonly byte[] Suffix =
    {
        0x51, 0x8D, 0x64, 0xA6, 0x35, 0xDE, 0xD8, 0xC1,
        0xE6, 0xB0, 0x39, 0xB1, 0xC3, 0xE5, 0x52, 0x30,
    };

    public static bool TryHash(string name, out string nid)
    {
        nid = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var nameBytes = Encoding.ASCII.GetBytes(name);
        var input = new byte[nameBytes.Length + Suffix.Length];
        Buffer.BlockCopy(nameBytes, 0, input, 0, nameBytes.Length);
        Buffer.BlockCopy(Suffix, 0, input, nameBytes.Length, Suffix.Length);

        // A self-contained SHA-1: the BCL's SHA1 dlopens the OpenSSL native shim
        // on Linux, which under Box64 drags in the host libssl wrapper. This hash
        // runs on the emulator's own resolution path, so keep it dependency-free.
        var digest = Sha1(input);

        Span<byte> qword = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            qword[i] = digest[7 - i];
        }

        nid = Convert.ToBase64String(qword).Substring(0, 11);
        return true;
    }

    private static byte[] Sha1(byte[] message)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;

        var originalBitLen = (ulong)message.Length * 8;
        var paddedLen = message.Length + 1;
        while (paddedLen % 64 != 56)
        {
            paddedLen++;
        }
        var padded = new byte[paddedLen + 8];
        Buffer.BlockCopy(message, 0, padded, 0, message.Length);
        padded[message.Length] = 0x80;
        for (var i = 0; i < 8; i++)
        {
            padded[padded.Length - 1 - i] = (byte)(originalBitLen >> (8 * i));
        }

        var w = new uint[80];
        for (var chunk = 0; chunk < padded.Length; chunk += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                var j = chunk + i * 4;
                w[i] = (uint)((padded[j] << 24) | (padded[j + 1] << 16) | (padded[j + 2] << 8) | padded[j + 3]);
            }
            for (var i = 16; i < 80; i++)
            {
                w[i] = Rotl(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
            }

            uint a = h0, b = h1, c = h2, d = h3, e = h4;
            for (var i = 0; i < 80; i++)
            {
                uint f, k;
                if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
                else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
                else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
                else { f = b ^ c ^ d; k = 0xCA62C1D6; }

                var temp = Rotl(a, 5) + f + e + k + w[i];
                e = d;
                d = c;
                c = Rotl(b, 30);
                b = a;
                a = temp;
            }

            h0 += a; h1 += b; h2 += c; h3 += d; h4 += e;
        }

        var digest = new byte[20];
        WriteBigEndian(digest, 0, h0);
        WriteBigEndian(digest, 4, h1);
        WriteBigEndian(digest, 8, h2);
        WriteBigEndian(digest, 12, h3);
        WriteBigEndian(digest, 16, h4);
        return digest;
    }

    private static uint Rotl(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
