using Emu86;

// テスト用の環境設定。
static class Ir
{
    // 翻訳キャッシュを「1 命令ずつ、最初の実行から IR」にする(高速コアの代わりに IR 解釈器で 1 命令を実行する比較用)。
    // コードを RAM に直接書き換えたら InvalidateAll を呼ぶこと(書き込み経路を通らないので無効化されない)。
    public static void SingleStep(EmuEnvironment env)
    {
        env.Translations.Enabled = true;
        env.Translations.BlockLimit = 1;
        env.Translations.Chain = false;
        env.Translations.SingleAfter = 1;
    }
}
