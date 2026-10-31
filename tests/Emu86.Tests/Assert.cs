// 全スイート共通の検証。失敗は例外で、ランナーがスイート名とケース名を添えて報告する。
static class Assert
{
    public static int Assertions;

    public static void Equal<T>(T expected, T actual, string name)
    {
        Assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Assertions++; return; }
        throw new Exception($"{name}: expected {typeof(T).Name}");
    }
}
