namespace Emu86;

// BIOS データ領域(BDA。物理 0x400 から)のうち、このエミュレータが読み書きする項目の番地。
// ビデオの項目は VGA BIOS スタブ(INT 10h)が保ち、キーバッファの項目は自動操作(BootAutomation)が見る。
static class Bda
{
    public const int KeyBufferHead = 0x41A;     // キーバッファの読み出し位置(ワード。0x400 からのオフセット)
    public const int KeyBufferTail = 0x41C;     // キーバッファの書き込み位置(ワード。読み出し位置と同じなら空)
    public const int VideoMode = 0x449;         // 現在のビデオモード
    public const int Columns = 0x44A;           // 桁数(ワード)
    public const int PageSize = 0x44C;          // 1 ページの大きさ(ワード)
    public const int PageStart = 0x44E;         // 表示ページの先頭(ワード)
    public const int CursorColumn = 0x450;      // カーソル位置(ページ 0 の列。以降 8 ページ分の列・行が並ぶ)
    public const int CursorRow = 0x451;         // カーソル位置(ページ 0 の行)
    public const int CursorShape = 0x460;       // カーソルの形(終了行、開始行)
    public const int ActivePage = 0x462;        // 表示ページ
    public const int CrtcPort = 0x463;          // CRTC のポート(ワード)
    public const int KeyBufferStart = 0x480;    // キーバッファの先頭(ワード。0 なら既定の 0x1E)
    public const int LastRow = 0x484;           // 行数 - 1
    public const int CharacterHeight = 0x485;   // 文字の高さ(ワード)
    public const int VideoControl = 0x487;      // ビデオ制御(bit7 = モード設定で画面を消さなかった)
    public const int VideoSwitches = 0x488;     // スイッチ、モード設定の制御、表示結合コードの表の番号
}
