using System.Security.Cryptography;

namespace PulseSharp;

internal static class EventIds
{
    public static Guid NewVersion7(DateTimeOffset now)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        var milliseconds = now.ToUnixTimeMilliseconds();
        var timeHigh = (int)(milliseconds >> 16);
        var timeLow = (short)(milliseconds & 0xFFFF);
        var versionAndRandom = (short)((((random[0] << 8) | random[1]) & 0x0FFF) | 0x7000);
        var variantAndRandom = (byte)((random[2] & 0x3F) | 0x80);

        return new Guid(
            timeHigh,
            timeLow,
            versionAndRandom,
            variantAndRandom,
            random[3],
            random[4],
            random[5],
            random[6],
            random[7],
            random[8],
            random[9]
        );
    }
}
