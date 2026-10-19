using System.Runtime.InteropServices;

namespace QuickShot.App;

internal static class BrowserFocus
{
    internal static bool TryFocusContent(string token, CancellationToken cancellation)
    {
        IAutomation? automation = null; IElement? root = null; IElements? elements = null; nint condition = 0;
        var matches = new List<IElement>();
        try
        {
            if (cancellation.IsCancellationRequested || !NativeMethods.IsExpectedEdgeForeground(token)) return false;
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!;
            automation = (IAutomation)Activator.CreateInstance(type)!;
            automation.ElementFromHandle(NativeMethods.GetForegroundWindow(), out root);
            automation.CreatePropertyCondition(30003, 50030, out condition); // ControlType == Document
            root.FindAll(4, condition, out elements); // descendants, restricted to this exact Edge window
            elements.Length(out var count);
            if (count > 100) return false;
            for (var i = 0; i < count; i++)
            {
                elements.GetElement(i, out var element);
                try
                {
                    element.GetCurrentPropertyValue(30005, out var name);
                    element.GetCurrentPropertyValue(30022, out var offscreen);
                    if (name is string text && text.StartsWith(token + " ", StringComparison.Ordinal) && offscreen is false)
                    { matches.Add(element); element = null!; }
                }
                finally { Release(element); }
            }
            if (matches.Count != 1 || cancellation.IsCancellationRequested || !NativeMethods.IsExpectedEdgeForeground(token)) return false;
            matches[0].SetFocus();
            return !cancellation.IsCancellationRequested && NativeMethods.IsExpectedEdgeForeground(token);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or OperationCanceledException) { return false; }
        finally
        {
            foreach (var element in matches) Release(element);
            Release(elements); Release(root);
            if (condition != 0) Marshal.Release(condition);
            Release(automation);
        }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        void ElementFromHandle(nint window, out IElement element);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void ControlViewWalker(); void ContentViewWalker(); void RawViewWalker();
        void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        void CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value, out nint condition);
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst();
        void FindAll(int scope, nint condition, out IElements elements);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        void GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElements
    {
        void Length(out int length);
        void GetElement(int index, out IElement element);
    }
}
