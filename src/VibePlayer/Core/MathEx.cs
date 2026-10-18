namespace VibePlayer;

/// <summary>Math.Clamp, which .NET Framework 4.8 doesn't have (the .NET 10 build still uses Math.Clamp).</summary>
internal static class MathEx
{
#if NETFRAMEWORK
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
    public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
#else
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    public static long Clamp(long value, long min, long max) => Math.Clamp(value, min, max);
    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    public static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);
#endif
}
