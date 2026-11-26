using System.Reflection;
using System.Runtime.InteropServices;

namespace BalatroPoker.Game.Graphics;

public static unsafe class Sdl2
{
    private const string LibName = "SDL2";

    public const uint SDL_INIT_AUDIO = 0x00000010;
    public const uint SDL_INIT_VIDEO = 0x00000020;
    public const ushort AUDIO_S16LSB = 0x8010;
    public const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;
    public const uint SDL_WINDOW_SHOWN = 0x00000004;
    public const uint SDL_WINDOW_RESIZABLE = 0x00000020;
    public const uint SDL_RENDERER_ACCELERATED = 0x00000002;
    public const uint SDL_RENDERER_PRESENTVSYNC = 0x00000004;
    public const uint SDL_PIXELFORMAT_ARGB8888 = 372645892;
    public const int SDL_TEXTUREACCESS_STREAMING = 1;

    public const uint SDL_QUIT = 0x100;
    public const uint SDL_KEYDOWN = 0x300;
    public const uint SDL_TEXTINPUT = 0x303;
    public const uint SDL_MOUSEMOTION = 0x400;
    public const uint SDL_MOUSEBUTTONDOWN = 0x401;
    public const uint SDL_MOUSEBUTTONUP = 0x402;
    public const uint SDL_MOUSEWHEEL = 0x403;

    public const int SDLK_RETURN = '\r';
    public const int SDLK_ESCAPE = 27;
    public const int SDLK_BACKSPACE = '\b';
    public const int SDLK_TAB = '\t';
    public const int SDLK_SPACE = ' ';
    public const int SDLK_F1 = 1073741882;
    public const int SDLK_F2 = 1073741883;
    public const int SDLK_UP = 1073741906;
    public const int SDLK_DOWN = 1073741905;

    static Sdl2()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sdl2).Assembly, ResolveSdl);
    }

    private static IntPtr ResolveSdl(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibName) return IntPtr.Zero;

        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "SDL2.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libSDL2.dylib", "libSDL2-2.0.0.dylib", "/opt/homebrew/lib/libSDL2.dylib" }
                : new[] { "libSDL2-2.0.so.0", "libSDL2.so", "libSDL2-2.0.so" };

        foreach (string name in candidates)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    public struct SDL_Event
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(0)] public SDL_KeyboardEvent key;
        [FieldOffset(0)] public SDL_TextInputEvent text;
        [FieldOffset(0)] public SDL_MouseMotionEvent motion;
        [FieldOffset(0)] public SDL_MouseButtonEvent button;
        [FieldOffset(0)] public SDL_MouseWheelEvent wheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_Keysym
    {
        public int scancode;
        public int sym;
        public ushort mod;
        public uint unused;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_KeyboardEvent
    {
        public uint type;
        public uint timestamp;
        public uint windowID;
        public byte state;
        public byte repeat;
        public byte padding2;
        public byte padding3;
        public SDL_Keysym keysym;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_TextInputEvent
    {
        public uint type;
        public uint timestamp;
        public uint windowID;
        public fixed byte text[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MouseMotionEvent
    {
        public uint type;
        public uint timestamp;
        public uint windowID;
        public uint which;
        public uint state;
        public int x;
        public int y;
        public int xrel;
        public int yrel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MouseButtonEvent
    {
        public uint type;
        public uint timestamp;
        public uint windowID;
        public uint which;
        public byte button;
        public byte state;
        public byte clicks;
        public byte padding1;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MouseWheelEvent
    {
        public uint type;
        public uint timestamp;
        public uint windowID;
        public uint which;
        public int x;
        public int y;
        public uint direction;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_AudioSpec
    {
        public int freq;
        public ushort format;
        public byte channels;
        public byte silence;
        public ushort samples;
        public ushort padding;
        public uint size;
        public IntPtr callback;
        public IntPtr userdata;
    }

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_Init(uint flags);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_InitSubSystem(uint flags);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint SDL_OpenAudioDevice(
        IntPtr device,
        int iscapture,
        ref SDL_AudioSpec desired,
        out SDL_AudioSpec obtained,
        int allowed_changes);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_PauseAudioDevice(uint dev, int pause_on);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_QueueAudio(uint dev, IntPtr data, uint len);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_CloseAudioDevice(uint dev);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Quit();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GetError();

    public static string GetErrorString() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "Unknown SDL error";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool SDL_SetHint(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_CreateWindow(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        int x, int y, int w, int h, uint flags);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_DestroyWindow(IntPtr window);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_DestroyRenderer(IntPtr renderer);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_RenderSetLogicalSize(IntPtr renderer, int w, int h);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_DestroyTexture(IntPtr texture);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_RenderClear(IntPtr renderer);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, IntPtr dstrect);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_RenderPresent(IntPtr renderer);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_PollEvent(out SDL_Event ev);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_StartTextInput();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Delay(uint ms);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong SDL_GetTicks64();
}
