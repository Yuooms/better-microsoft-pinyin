using System.Runtime.InteropServices;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Interop.Uia;

/// <summary>
/// UI Automation 的树范围。
/// </summary>
[Flags]
public enum TreeScope
{
    /// <summary>元素自身。</summary>
    Element = 0x1,

    /// <summary>直接子元素。</summary>
    Children = 0x2,

    /// <summary>所有后代。</summary>
    Descendants = 0x4,

    /// <summary>父元素。</summary>
    Parent = 0x8,

    /// <summary>祖先元素。</summary>
    Ancestors = 0x10,

    /// <summary>元素自身与所有后代。</summary>
    Subtree = Element | Children | Descendants,
}

/// <summary>
/// UIA 条件对象。此处只作为句柄使用。
/// </summary>
[ComImport]
[Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationCondition
{
}

/// <summary>
/// UIA 元素集合。
/// </summary>
[ComImport]
[Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElementArray
{
    /// <summary>元素个数。</summary>
    [PreserveSig]
    int get_Length(out int length);

    /// <summary>按下标取元素。</summary>
    [PreserveSig]
    int GetElement(int index, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
}

/// <summary>
/// UIA 元素。
/// </summary>
/// <remarks>
/// 只声明到实际用到的槽位（BoundingRectangle），其余同序声明用于占位保证 vtable 对齐。
/// 未被调用的方法即使签名不精确也不会被编组，因此不影响运行。
/// </remarks>
[ComImport]
[Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElement
{
    /// <summary>把焦点设到该元素。</summary>
    [PreserveSig]
    int SetFocus();

    /// <summary>获取运行时 Id。</summary>
    [PreserveSig]
    int GetRuntimeId(out IntPtr runtimeId);

    /// <summary>查找第一个匹配的后代。</summary>
    [PreserveSig]
    int FindFirst(
        TreeScope scope,
        IUIAutomationCondition condition,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement found);

    /// <summary>查找所有匹配的后代。</summary>
    [PreserveSig]
    int FindAll(
        TreeScope scope,
        IUIAutomationCondition condition,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElementArray found);

    /// <summary>带缓存查找第一个。</summary>
    [PreserveSig]
    int FindFirstBuildCache(TreeScope scope, IUIAutomationCondition condition, IntPtr cacheRequest, out IntPtr found);

    /// <summary>带缓存查找全部。</summary>
    [PreserveSig]
    int FindAllBuildCache(TreeScope scope, IUIAutomationCondition condition, IntPtr cacheRequest, out IntPtr found);

    /// <summary>刷新缓存。</summary>
    [PreserveSig]
    int BuildUpdatedCache(IntPtr cacheRequest, out IntPtr updatedElement);

    /// <summary>读属性（VARIANT）。</summary>
    [PreserveSig]
    int GetCurrentPropertyValue(int propertyId, out IntPtr value);

    /// <summary>读属性（可忽略默认值）。</summary>
    [PreserveSig]
    int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out IntPtr value);

    /// <summary>读缓存属性。</summary>
    [PreserveSig]
    int GetCachedPropertyValue(int propertyId, out IntPtr value);

    /// <summary>读缓存属性（可忽略默认值）。</summary>
    [PreserveSig]
    int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out IntPtr value);

    /// <summary>取控件模式（强类型）。</summary>
    [PreserveSig]
    int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr pattern);

    /// <summary>取缓存控件模式（强类型）。</summary>
    [PreserveSig]
    int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr pattern);

    /// <summary>取控件模式。</summary>
    [PreserveSig]
    int GetCurrentPattern(int patternId, out IntPtr pattern);

    /// <summary>取缓存控件模式。</summary>
    [PreserveSig]
    int GetCachedPattern(int patternId, out IntPtr pattern);

    /// <summary>缓存父元素。</summary>
    [PreserveSig]
    int GetCachedParent(out IntPtr parent);

    /// <summary>缓存子元素。</summary>
    [PreserveSig]
    int GetCachedChildren(out IntPtr children);

    /// <summary>进程 Id。</summary>
    [PreserveSig]
    int get_CurrentProcessId(out int value);

    /// <summary>控件类型。</summary>
    [PreserveSig]
    int get_CurrentControlType(out int value);

    /// <summary>本地化控件类型名。</summary>
    [PreserveSig]
    int get_CurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>名称。候选词文本即从此属性取得。</summary>
    [PreserveSig]
    int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>快捷键。</summary>
    [PreserveSig]
    int get_CurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>访问键。</summary>
    [PreserveSig]
    int get_CurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>是否拥有键盘焦点。</summary>
    [PreserveSig]
    int get_CurrentHasKeyboardFocus(out int value);

    /// <summary>是否可聚焦。</summary>
    [PreserveSig]
    int get_CurrentIsKeyboardFocusable(out int value);

    /// <summary>是否启用。</summary>
    [PreserveSig]
    int get_CurrentIsEnabled(out int value);

    /// <summary>AutomationId。候选窗的标识为 <c>IME_Candidate_Window</c>。</summary>
    [PreserveSig]
    int get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>窗口类名。</summary>
    [PreserveSig]
    int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>帮助文本。</summary>
    [PreserveSig]
    int get_CurrentHelpText([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>区域性。</summary>
    [PreserveSig]
    int get_CurrentCulture(out int value);

    /// <summary>是否控件元素。</summary>
    [PreserveSig]
    int get_CurrentIsControlElement(out int value);

    /// <summary>是否内容元素。</summary>
    [PreserveSig]
    int get_CurrentIsContentElement(out int value);

    /// <summary>是否密码框。</summary>
    [PreserveSig]
    int get_CurrentIsPassword(out int value);

    /// <summary>原生窗口句柄。</summary>
    [PreserveSig]
    int get_CurrentNativeWindowHandle(out IntPtr value);

    /// <summary>项目类型。</summary>
    [PreserveSig]
    int get_CurrentItemType([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>是否在屏幕外（不可见）。</summary>
    [PreserveSig]
    int get_CurrentIsOffscreen(out int value);

    /// <summary>方向。</summary>
    [PreserveSig]
    int get_CurrentOrientation(out int value);

    /// <summary>框架 Id。</summary>
    [PreserveSig]
    int get_CurrentFrameworkId([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>是否表单必填。</summary>
    [PreserveSig]
    int get_CurrentIsRequiredForForm(out int value);

    /// <summary>项目状态。</summary>
    [PreserveSig]
    int get_CurrentItemStatus([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>屏幕包围盒（物理像素）。定位浮窗时使用。</summary>
    [PreserveSig]
    int get_CurrentBoundingRectangle(out NativeRect value);
}

/// <summary>
/// UIA 根对象。
/// </summary>
[ComImport]
[Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomation
{
    /// <summary>比较两个元素。</summary>
    [PreserveSig]
    int CompareElements(IntPtr first, IntPtr second, out int areSame);

    /// <summary>比较运行时 Id。</summary>
    [PreserveSig]
    int CompareRuntimeIds(IntPtr first, IntPtr second, out int areSame);

    /// <summary>取桌面根元素。</summary>
    [PreserveSig]
    int GetRootElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement root);

    /// <summary>由窗口句柄取元素。</summary>
    [PreserveSig]
    int ElementFromHandle(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>由屏幕坐标取元素。</summary>
    [PreserveSig]
    int ElementFromPoint(IntPtr point, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>取当前焦点元素。</summary>
    [PreserveSig]
    int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>带缓存取根元素。</summary>
    [PreserveSig]
    int GetRootElementBuildCache(IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>带缓存由句柄取元素。</summary>
    [PreserveSig]
    int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>带缓存由坐标取元素。</summary>
    [PreserveSig]
    int ElementFromPointBuildCache(IntPtr point, IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>带缓存取焦点元素。</summary>
    [PreserveSig]
    int GetFocusedElementBuildCache(IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);

    /// <summary>创建树遍历器。</summary>
    [PreserveSig]
    int CreateTreeWalker(IntPtr condition, out IntPtr walker);

    /// <summary>控件视图遍历器。</summary>
    [PreserveSig]
    int get_ControlViewWalker(out IntPtr walker);

    /// <summary>内容视图遍历器。</summary>
    [PreserveSig]
    int get_ContentViewWalker(out IntPtr walker);

    /// <summary>原始视图遍历器。</summary>
    [PreserveSig]
    int get_RawViewWalker(out IntPtr walker);

    /// <summary>原始视图条件。</summary>
    [PreserveSig]
    int get_RawViewCondition([MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition condition);

    /// <summary>控件视图条件。</summary>
    [PreserveSig]
    int get_ControlViewCondition([MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition condition);

    /// <summary>内容视图条件。</summary>
    [PreserveSig]
    int get_ContentViewCondition([MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition condition);

    /// <summary>创建缓存请求。</summary>
    [PreserveSig]
    int CreateCacheRequest(out IntPtr cacheRequest);

    /// <summary>创建「恒真」条件，等价于不过滤。</summary>
    [PreserveSig]
    int CreateTrueCondition([MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition condition);
}

/// <summary>
/// 创建 UI Automation 根对象的工厂。
/// </summary>
public static class UiaFactory
{
    private static readonly Guid ClsidCUiaAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    /// <summary>
    /// 创建 <see cref="IUIAutomation"/> 实例。
    /// </summary>
    /// <remarks>调用线程必须是 STA，否则跨进程 UIA 调用会出现不可预期的阻塞。</remarks>
    public static IUIAutomation Create()
    {
        var type = Type.GetTypeFromCLSID(ClsidCUiaAutomation, throwOnError: true)!;
        return (IUIAutomation)Activator.CreateInstance(type)!;
    }
}

/// <summary>UIA 的控件模式标识。</summary>
public static class UiaInterop
{
    /// <summary><c>UIA_ValuePatternId</c>：读写控件文本。</summary>
    public const int ValuePatternId = 10002;
}

/// <summary>
/// <c>IUIAutomationValuePattern</c>：只用来读控件文本，不写。
/// </summary>
/// <remarks>
/// vtable 顺序必须与 UIA 头文件一致：SetValue、CurrentValue、CurrentIsReadOnly。
/// 接口方法不能省略，否则后续方法的槽位会整体前移，调用到错误函数。
/// </remarks>
[ComImport]
[Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationValuePattern
{
    /// <summary>设置值（本程序不会调用）。</summary>
    [PreserveSig]
    int SetValue([MarshalAs(UnmanagedType.BStr)] string value);

    /// <summary>读取当前值。</summary>
    [PreserveSig]
    int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);

    /// <summary>是否只读。</summary>
    [PreserveSig]
    int get_CurrentIsReadOnly(out int value);
}
