using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WinTrace
{
    /// <summary>Finds the user code that issued a step, from the current call stack.</summary>
    static class CallSite
    {
        static readonly string[] SkippedPrefixes =
        {
            "WinTrace.", "System.", "Microsoft.", "OpenQA.", "NUnit.", "Xunit.", "xunit.", "Castle.", "Moq."
        };

        /// <summary>"File.cs:12 ← Caller.cs:34", innermost user frame first; null when no frame has source info.</summary>
        public static string? Describe(int depth)
        {
            if (depth <= 0) return null;
            StackFrame[] frames;
            try { frames = new StackTrace(2, true).GetFrames() ?? Array.Empty<StackFrame>(); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { return null; }

            var parts = new List<string>();
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method == null || IsSkipped(method)) continue;
                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                if (string.IsNullOrEmpty(file) || line <= 0) continue;
                var label = Path.GetFileName(file.Replace('\\', '/')) + ":" + line;
                if (parts.Count == 0 || parts[parts.Count - 1] != label) parts.Add(label);
                if (parts.Count >= depth) break;
            }
            return parts.Count == 0 ? null : string.Join(" ← ", parts);
        }

        static bool IsSkipped(MethodBase method)
        {
            var type = method.DeclaringType;
            // Compiler-generated closures and state machines belong to the method that declared them.
            while (type != null && type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null) type = type.DeclaringType;
            var name = type?.FullName ?? "";
            foreach (var prefix in SkippedPrefixes)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return HasHidingAttribute(method) || (type != null && HasHidingAttribute(type));
        }

        // By name: StackTraceHiddenAttribute is not part of netstandard2.0.
        static bool HasHidingAttribute(MemberInfo member)
        {
            try
            {
                foreach (var a in member.GetCustomAttributes(false))
                {
                    var n = a.GetType().Name;
                    if (n == "StackTraceHiddenAttribute" || n == "DebuggerStepThroughAttribute" || n == "DebuggerHiddenAttribute") return true;
                }
            }
            catch (Exception ex) when (ex is TypeLoadException || ex is FileNotFoundException || ex is CustomAttributeFormatException) { }
            return false;
        }
    }
}
