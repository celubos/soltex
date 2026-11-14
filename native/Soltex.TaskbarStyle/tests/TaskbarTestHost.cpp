// Test host for the taskbar look helper: a hidden, off-screen window hosting system XAML (the same Windows.UI.Xaml the
// taskbar uses) with a copy of the taskbar's background structure:
//   Grid#TaskbarBackground > Grid > Rectangle#BackgroundFill, Rectangle#BackgroundStroke
// Tests inject the helper into this process instead of Explorer and read the real brushes back through the XAML API.
//
// stdin commands: "fill" (prints the BackgroundFill and BackgroundStroke brushes; acrylic as source, tint, tint opacity and
// fallback colour), "pixel" (renders the window and prints
// the centre pixel), "probe" (opacity, visibility and width of the style probes Inside and Outside), "quit". stdout: "ready <pid>", then one line per command.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <inspectable.h>
#include <roapi.h>
#include <stdio.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#include <winstring.h>

namespace {

// Interface IDs and method order from C:\Windows\System32\WinMetadata\Windows.UI.Xaml.winmd.
constexpr IID IidWindowsXamlManagerStatics = {0x28258a12, 0x7d82, 0x505b, {0xb2, 0x10, 0x71, 0x2b, 0x04, 0xa5, 0x88, 0x82}};
constexpr IID IidDesktopWindowXamlSourceFactory = {0x5cd61dc0, 0x2561, 0x56e1, {0x8e, 0x75, 0x6e, 0x44, 0x17, 0x38, 0x05, 0xe3}};
constexpr IID IidDesktopWindowXamlSource = {0xd585bfe1, 0x00ff, 0x51be, {0xba, 0x1d, 0xa1, 0x32, 0x99, 0x56, 0xea, 0x0a}};
constexpr IID IidXamlReaderStatics = {0x9891c6bd, 0x534f, 0x4955, {0xb8, 0x5a, 0x8a, 0x8d, 0xc0, 0xdc, 0xa6, 0x02}};
constexpr IID IidUIElement = {0x676d0be9, 0xb65c, 0x41c6, {0xba, 0x40, 0x58, 0xcf, 0x87, 0xf2, 0x01, 0xc1}};
constexpr IID IidFrameworkElement = {0xa391d09b, 0x4a99, 0x4b7c, {0x9d, 0x8d, 0x6f, 0xa5, 0xd0, 0x1f, 0x6f, 0xbf}};
constexpr IID IidShape = {0x786f2b75, 0x9aa0, 0x454d, {0xae, 0x06, 0xa2, 0x46, 0x6e, 0x37, 0xc8, 0x32}};
constexpr IID IidSolidColorBrush = {0x9d850850, 0x66f3, 0x48df, {0x9a, 0x8f, 0x82, 0x4b, 0xd5, 0xe0, 0x70, 0xaf}};
constexpr IID IidAcrylicBrush = {0x79bbcf4e, 0xcd66, 0x4f1b, {0xa8, 0xb6, 0xcd, 0x6d, 0x29, 0x77, 0xc1, 0x8d}};
constexpr IID IidXamlCompositionBrushBase = {0x03e432d9, 0xb35c, 0x4a79, {0x81, 0x1c, 0xc5, 0x65, 0x20, 0x04, 0xda, 0x0e}};

struct IWindowsXamlManagerStatics : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE InitializeForCurrentThread(IInspectable **manager) = 0;
};
struct IDesktopWindowXamlSourceFactory : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE CreateInstance(IInspectable *outer, IInspectable **inner, IInspectable **instance) = 0;
};
struct IDesktopWindowXamlSource : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_Content(IInspectable **value) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_Content(IInspectable *value) = 0;
};
struct IXamlReaderStatics : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE Load(HSTRING xaml, IInspectable **result) = 0;
};
struct IShape : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_Fill(IInspectable **brush) = 0;
};
struct XamlColor
{
    BYTE A, R, G, B;
};
struct ISolidColorBrush : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_Color(XamlColor *color) = 0;
};
struct IAcrylicBrush : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_BackgroundSource(int *value) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_BackgroundSource(int value) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_TintColor(XamlColor *value) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_TintColor(XamlColor value) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_TintOpacity(double *value) = 0;
};
struct IXamlCompositionBrushBase : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_FallbackColor(XamlColor *value) = 0;
};
constexpr int FindNameSlot = 6 + 45; // IInspectable (6) + IFrameworkElement.FindName (method 45)

const wchar_t Markup[] =
    L"<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
    L"xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='#FF101010'>"
    L"<Grid x:Name='TaskbarBackground'><Grid>"
    L"<Rectangle x:Name='BackgroundFill' Fill='#FF202020' />"
    L"<Rectangle x:Name='BackgroundStroke' Fill='#FF404040' Height='1' VerticalAlignment='Top' />"
    L"</Grid></Grid>"
    // Style rule probes (top-left, away from the centre pixel the brush tests read).
    L"<StackPanel x:Name='StyleScope' HorizontalAlignment='Left' VerticalAlignment='Top'>"
    L"<Border x:Name='Inside' Width='40' Height='4' Background='#FF00FF00' /></StackPanel>"
    L"<Border x:Name='Outside' Width='40' Height='4' HorizontalAlignment='Right' VerticalAlignment='Top' Background='#FF0000FF' />"
    // A template part whose CornerRadius is a TemplateBinding, like the taskbar's button backgrounds.
    L"<ContentControl x:Name='Templated' CornerRadius='3' HorizontalAlignment='Left' VerticalAlignment='Bottom'><ContentControl.Template>"
    L"<ControlTemplate TargetType='ContentControl'><Border x:Name='TemplatedBorder' Width='10' Height='4' Background='#FFFF00FF' "
    L"CornerRadius='{TemplateBinding CornerRadius}' /></ControlTemplate></ContentControl.Template></ContentControl>"
    L"</Grid>";

constexpr UINT CommandMessage = WM_APP + 1;
constexpr UINT RenderFullContent = 0x2; // PW_RENDERFULLCONTENT (not in the MinGW headers)
HWND g_window;
IShape *g_fill, *g_stroke;
IInspectable *g_inside, *g_outside, *g_templated;
constexpr IID IidVisualTreeHelperStatics = {0xe75758c4, 0xd25d, 0x4b1d, {0x97, 0x1f, 0x59, 0x6f, 0x17, 0xf1, 0x2b, 0xaa}};
constexpr IID IidDependencyObject = {0x5c526665, 0xf60e, 0x4912, {0xaf, 0x59, 0x5f, 0xe0, 0x68, 0x0f, 0x08, 0x9d}};
constexpr IID IidBorder = {0x797c4539, 0x45bd, 0x4633, {0xa0, 0x44, 0xbf, 0xb0, 0x2e, 0xf5, 0x17, 0x0f}};
constexpr int GetChildSlot = 6 + 4, CornerRadiusSlot = 6 + 6; // VisualTreeHelper.GetChild, IBorder.get_CornerRadius
struct XamlCornerRadius
{
    double TopLeft, TopRight, BottomRight, BottomLeft;
};
constexpr int OpacitySlot = 6 + 3, VisibilitySlot = 6 + 15, WidthSlot = 6 + 9; // IUIElement.get_Opacity/get_Visibility, IFrameworkElement.get_Width

void Fail(const char *step, HRESULT hr)
{
    printf("error %s 0x%08lX\n", step, static_cast<unsigned long>(hr));
    fflush(stdout);
    ExitProcess(1);
}

HSTRING Text(const wchar_t *text)
{
    HSTRING value = nullptr;
    if (FAILED(WindowsCreateString(text, static_cast<UINT32>(wcslen(text)), &value))) Fail("string", E_OUTOFMEMORY);
    return value;
}

template <typename T> T *Factory(const wchar_t *runtimeClass, const IID &iid)
{
    HSTRING name = Text(runtimeClass);
    void *factory = nullptr;
    HRESULT hr = RoGetActivationFactory(name, iid, &factory);
    WindowsDeleteString(name);
    if (FAILED(hr)) Fail("factory", hr);
    return static_cast<T *>(factory);
}

IInspectable *FindElement(IInspectable *root, const wchar_t *name)
{
    void *element = nullptr;
    if (FAILED(root->QueryInterface(IidFrameworkElement, &element))) Fail("frameworkelement", E_NOINTERFACE);
    using FindName = HRESULT(STDMETHODCALLTYPE *)(void *, HSTRING, IInspectable **);
    FindName find = reinterpret_cast<FindName>((*static_cast<void ***>(element))[FindNameSlot]);
    HSTRING text = Text(name);
    IInspectable *found = nullptr;
    HRESULT hr = find(element, text, &found);
    WindowsDeleteString(text);
    static_cast<IUnknown *>(element)->Release();
    if (FAILED(hr) || !found) Fail("findname", FAILED(hr) ? hr : E_FAIL);
    return found;
}

// "o:<opacity>,v:<visibility>,w:<width>" read through the element's own XAML interfaces.
void PrintProbe(const char *label, IInspectable *element)
{
    void *ui = nullptr, *framework = nullptr;
    double opacity = -1, width = -1;
    int visibility = -1;
    if (SUCCEEDED(element->QueryInterface(IidUIElement, &ui)))
    {
        void **table = *static_cast<void ***>(ui);
        reinterpret_cast<HRESULT(STDMETHODCALLTYPE *)(void *, double *)>(table[OpacitySlot])(ui, &opacity);
        reinterpret_cast<HRESULT(STDMETHODCALLTYPE *)(void *, int *)>(table[VisibilitySlot])(ui, &visibility);
        static_cast<IUnknown *>(ui)->Release();
    }
    if (SUCCEEDED(element->QueryInterface(IidFrameworkElement, &framework)))
    {
        reinterpret_cast<HRESULT(STDMETHODCALLTYPE *)(void *, double *)>((*static_cast<void ***>(framework))[WidthSlot])(framework, &width);
        static_cast<IUnknown *>(framework)->Release();
    }
    printf("%s=o:%.2f,v:%d,w:%.0f", label, opacity, visibility, width);
}

// The template part's corner radius ("templated=r:<top left>"), or -1 when the template is not applied yet.
void PrintTemplated()
{
    double radius = -1;
    void *statics = nullptr, *reference = nullptr, *child = nullptr, *border = nullptr;
    HSTRING name = Text(L"Windows.UI.Xaml.Media.VisualTreeHelper");
    if (SUCCEEDED(RoGetActivationFactory(name, IidVisualTreeHelperStatics, &statics)) && SUCCEEDED(g_templated->QueryInterface(IidDependencyObject, &reference)))
    {
        using GetChild = HRESULT(STDMETHODCALLTYPE *)(void *, void *, int, void **);
        if (SUCCEEDED(reinterpret_cast<GetChild>((*static_cast<void ***>(statics))[GetChildSlot])(statics, reference, 0, &child)) && child &&
            SUCCEEDED(static_cast<IUnknown *>(child)->QueryInterface(IidBorder, &border)))
        {
            XamlCornerRadius corners{};
            reinterpret_cast<HRESULT(STDMETHODCALLTYPE *)(void *, XamlCornerRadius *)>((*static_cast<void ***>(border))[CornerRadiusSlot])(border, &corners);
            radius = corners.TopLeft;
        }
    }
    WindowsDeleteString(name);
    void *held[] = {statics, reference, child, border};
    for (void *p : held)
        if (p) static_cast<IUnknown *>(p)->Release();
    printf(" templated=r:%.0f", radius);
}

IShape *FindShape(IInspectable *root, const wchar_t *name)
{
    void *element = nullptr;
    if (FAILED(root->QueryInterface(IidFrameworkElement, &element))) Fail("frameworkelement", E_NOINTERFACE);
    using FindName = HRESULT(STDMETHODCALLTYPE *)(void *, HSTRING, IInspectable **);
    FindName find = reinterpret_cast<FindName>((*static_cast<void ***>(element))[FindNameSlot]);
    HSTRING text = Text(name);
    IInspectable *found = nullptr;
    HRESULT hr = find(element, text, &found);
    WindowsDeleteString(text);
    static_cast<IUnknown *>(element)->Release();
    if (FAILED(hr) || !found) Fail("findname", FAILED(hr) ? hr : E_FAIL);
    void *shape = nullptr;
    hr = found->QueryInterface(IidShape, &shape);
    found->Release();
    if (FAILED(hr)) Fail("shape", hr);
    return static_cast<IShape *>(shape);
}

void PrintBrush(const char *label, IShape *shape)
{
    IInspectable *brush = nullptr;
    HRESULT hr = shape->get_Fill(&brush);
    if (FAILED(hr)) { printf("%s=error:0x%08lX", label, static_cast<unsigned long>(hr)); return; }
    if (!brush) { printf("%s=null", label); return; }
    void *solid = nullptr, *acrylic = nullptr;
    if (SUCCEEDED(brush->QueryInterface(IidAcrylicBrush, &acrylic)))
    {
        int source = -1;
        XamlColor tint{}, fallback{};
        double opacity = -1;
        IAcrylicBrush *value = static_cast<IAcrylicBrush *>(acrylic);
        value->get_BackgroundSource(&source);
        value->get_TintColor(&tint);
        value->get_TintOpacity(&opacity);
        value->Release();
        void *composition = nullptr; // FallbackColor: what XAML shows when acrylic cannot render
        if (SUCCEEDED(brush->QueryInterface(IidXamlCompositionBrushBase, &composition)))
        {
            static_cast<IXamlCompositionBrushBase *>(composition)->get_FallbackColor(&fallback);
            static_cast<IUnknown *>(composition)->Release();
        }
        printf("%s=acrylic:source=%d,tint=#%02X%02X%02X%02X,opacity=%.3f,fallback=#%02X%02X%02X%02X", label, source, tint.A, tint.R, tint.G,
               tint.B, opacity, fallback.A, fallback.R, fallback.G, fallback.B);
    }
    else if (SUCCEEDED(brush->QueryInterface(IidSolidColorBrush, &solid)))
    {
        XamlColor color{};
        static_cast<ISolidColorBrush *>(solid)->get_Color(&color);
        static_cast<IUnknown *>(solid)->Release();
        printf("%s=#%02X%02X%02X%02X", label, color.A, color.R, color.G, color.B);
    }
    else
    {
        HSTRING name = nullptr;
        brush->GetRuntimeClassName(&name);
        printf("%s=class:%ls", label, WindowsGetStringRawBuffer(name, nullptr));
        WindowsDeleteString(name);
    }
    brush->Release();
}

void PrintPixel()
{
    RECT bounds;
    GetClientRect(g_window, &bounds);
    HDC screen = GetDC(nullptr), memory = CreateCompatibleDC(screen);
    HBITMAP bitmap = CreateCompatibleBitmap(screen, bounds.right, bounds.bottom);
    HGDIOBJ old = SelectObject(memory, bitmap);
    BOOL rendered = PrintWindow(g_window, memory, PW_CLIENTONLY | RenderFullContent);
    COLORREF pixel = GetPixel(memory, bounds.right / 2, bounds.bottom / 2);
    SelectObject(memory, old);
    DeleteObject(bitmap);
    DeleteDC(memory);
    ReleaseDC(nullptr, screen);
    printf("pixel=%s#%02X%02X%02X\n", rendered ? "" : "unrendered:", GetRValue(pixel), GetGValue(pixel), GetBValue(pixel));
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == CommandMessage)
    {
        if (wParam == 1)
        {
            PrintBrush("fill", g_fill);
            printf(" ");
            PrintBrush("stroke", g_stroke);
            printf("\n");
        }
        else if (wParam == 2) PrintPixel();
        else if (wParam == 3)
        {
            PrintProbe("inside", g_inside);
            printf(" ");
            PrintProbe("outside", g_outside);
            PrintTemplated();
            printf("\n");
        }
        fflush(stdout);
        return 0;
    }
    if (message == WM_TIMER) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

DWORD WINAPI ReadCommands(void *)
{
    char line[64];
    while (fgets(line, sizeof line, stdin))
    {
        if (strncmp(line, "fill", 4) == 0) PostMessageW(g_window, CommandMessage, 1, 0);
        else if (strncmp(line, "pixel", 5) == 0) PostMessageW(g_window, CommandMessage, 2, 0);
        else if (strncmp(line, "probe", 5) == 0) PostMessageW(g_window, CommandMessage, 3, 0);
        else if (strncmp(line, "quit", 4) == 0) break;
    }
    PostMessageW(g_window, WM_CLOSE, 0, 0);
    return 0;
}

} // namespace

// "--linger <seconds>": no stdin commands; stay up for that long (for runs started without a console).
int wmain(int argc, wchar_t **argv)
{
    int linger = argc == 3 && wcscmp(argv[1], L"--linger") == 0 ? _wtoi(argv[2]) : 0;
    HRESULT hr = RoInitialize(RO_INIT_SINGLETHREADED);
    if (FAILED(hr)) Fail("roinitialize", hr);

    IInspectable *manager = nullptr;
    hr = Factory<IWindowsXamlManagerStatics>(L"Windows.UI.Xaml.Hosting.WindowsXamlManager", IidWindowsXamlManagerStatics)->InitializeForCurrentThread(&manager);
    if (FAILED(hr)) Fail("xamlmanager", hr);

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"SoltexTaskbarTestHost";
    RegisterClassW(&windowClass);
    // Off-screen and never activated: nothing appears on the user's desktop.
    g_window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, windowClass.lpszClassName, L"Soltex taskbar test", WS_POPUP,
                               -12000, -12000, 320, 48, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (!g_window) Fail("window", HRESULT_FROM_WIN32(GetLastError()));

    IInspectable *inner = nullptr, *source = nullptr;
    hr = Factory<IDesktopWindowXamlSourceFactory>(L"Windows.UI.Xaml.Hosting.DesktopWindowXamlSource", IidDesktopWindowXamlSourceFactory)
             ->CreateInstance(nullptr, &inner, &source);
    if (FAILED(hr)) Fail("xamlsource", hr);
    IDesktopWindowXamlSourceNative *native = nullptr;
    hr = source->QueryInterface(__uuidof(IDesktopWindowXamlSourceNative), reinterpret_cast<void **>(&native));
    if (FAILED(hr)) Fail("native", hr);
    hr = native->AttachToWindow(g_window);
    if (FAILED(hr)) Fail("attach", hr);
    HWND island = nullptr;
    native->get_WindowHandle(&island);
    SetWindowPos(island, nullptr, 0, 0, 320, 48, SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOZORDER);

    HSTRING markup = Text(Markup);
    IInspectable *root = nullptr;
    hr = Factory<IXamlReaderStatics>(L"Windows.UI.Xaml.Markup.XamlReader", IidXamlReaderStatics)->Load(markup, &root);
    WindowsDeleteString(markup);
    if (FAILED(hr)) Fail("xamlreader", hr);
    void *element = nullptr, *xamlSource = nullptr;
    if (FAILED(root->QueryInterface(IidUIElement, &element))) Fail("uielement", E_NOINTERFACE);
    if (FAILED(source->QueryInterface(IidDesktopWindowXamlSource, &xamlSource))) Fail("sourceinterface", E_NOINTERFACE);
    hr = static_cast<IDesktopWindowXamlSource *>(xamlSource)->put_Content(static_cast<IInspectable *>(element));
    if (FAILED(hr)) Fail("content", hr);
    g_fill = FindShape(root, L"BackgroundFill");
    g_stroke = FindShape(root, L"BackgroundStroke");
    g_inside = FindElement(root, L"Inside");
    g_outside = FindElement(root, L"Outside");
    g_templated = FindElement(root, L"Templated");

    ShowWindow(g_window, SW_SHOWNOACTIVATE);
    printf("ready %lu %lu\n", GetCurrentProcessId(), GetCurrentThreadId());
    fflush(stdout);
    if (linger > 0) SetTimer(g_window, 1, static_cast<UINT>(linger) * 1000, nullptr);
    else if (HANDLE reader = CreateThread(nullptr, 0, ReadCommands, nullptr, 0, nullptr)) CloseHandle(reader);

    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return 0;
}
