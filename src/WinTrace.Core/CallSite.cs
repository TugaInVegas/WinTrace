using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// "File.cs:12 ← Caller.cs:34", innermost user frame first; null when no frame has source info. Lambda bodies are
        /// left out (their enclosing method is what matters), and consecutive frames of one type collapse to the outermost,
        /// the method other code called.
        /// </summary>
        public static string? Describe(int depth, ICollection<string>? skip = null)
        {
            if (depth <= 0) return null;
            StackFrame[] frames;
            try { frames = new StackTrace(2, true).GetFrames() ?? Array.Empty<StackFrame>(); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { return null; }

            var parts = new List<(Type? Type, string Label)>();
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method == null) continue;
                // Past an async continuation boundary the frames belong to whatever completed the awaited task, not to
                // the code that issued the step (e.g. the last awaited page method of the test).
                if (parts.Count > 0 && IsContinuationBoundary(method)) break;
                if (IsSkipped(method, skip) || IsLambda(method)) continue;
                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                if (string.IsNullOrEmpty(file) || line <= 0) continue;
                var type = OwnerType(method);
                var label = Path.GetFileName(file.Replace('\\', '/')) + ":" + line;
                if (parts.Count > 0 && parts[parts.Count - 1].Type == type && type != null)
                {
                    parts[parts.Count - 1] = (type, label);   // same type again: keep the outer call
                    continue;
                }
                if (parts.Count >= depth) break;
                parts.Add((type, label));
                if (IsTestEntry(method)) break;   // nothing past the test method is about this step
            }
            return parts.Count == 0 ? null : string.Join(" ← ", parts.Select(p => p.Label));
        }

        static readonly HashSet<string> ContinuationMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "SetResult", "SetException", "TrySetResult", "TrySetException", "SetExistingTaskResult", "FinishContinuations",
            "RunContinuations", "RunOrScheduleAction", "InvokeMoveNext", "ExecuteWithThreadLocal", "ExecuteEntryUnsafe"
        };

        static bool IsContinuationBoundary(MethodBase method)
        {
            var type = method.DeclaringType?.FullName ?? "";
            if (!type.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal)
                && !type.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal)
                && !type.StartsWith("System.Threading.ExecutionContext", StringComparison.Ordinal)) return false;
            return ContinuationMethods.Contains(method.Name) || type.IndexOf("Continuation", StringComparison.Ordinal) >= 0;
        }

        static readonly HashSet<string> TestEntryAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "TestMethodAttribute", "DataTestMethodAttribute", "TestInitializeAttribute", "TestCleanupAttribute",
            "ClassInitializeAttribute", "ClassCleanupAttribute", "AssemblyInitializeAttribute", "AssemblyCleanupAttribute",
            "TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute", "SetUpAttribute", "TearDownAttribute",
            "OneTimeSetUpAttribute", "OneTimeTearDownAttribute", "FactAttribute", "TheoryAttribute"
        };

        // A test, setup or cleanup method (also when the frame is its async state machine), with any attribute derived from those.
        static bool IsTestEntry(MethodBase method)
        {
            var candidates = new List<MethodBase> { method };
            var sm = method.DeclaringType;
            if (method.Name == "MoveNext" && sm != null && sm.Name.StartsWith("<", StringComparison.Ordinal) && sm.DeclaringType != null)
            {
                var end = sm.Name.IndexOf('>');
                if (end > 1)
                {
                    var name = sm.Name.Substring(1, end - 1);
                    candidates.AddRange(sm.DeclaringType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name == name));
                }
            }
            foreach (var m in candidates)
            {
                object[] attrs;
                try { attrs = m.GetCustomAttributes(true); }
                catch (Exception ex) when (ex is TypeLoadException || ex is FileNotFoundException || ex is CustomAttributeFormatException) { continue; }
                foreach (var a in attrs)
                    for (var t = a.GetType(); t != null && t != typeof(Attribute); t = t.BaseType)
                        if (TestEntryAttributes.Contains(t.Name)) return true;
            }
            return false;
        }

        // Lambda bodies compile to methods named like "<Outer>b__0_0" (in a closure class or the declaring type).
        static bool IsLambda(MethodBase method) => method.Name.StartsWith("<", StringComparison.Ordinal) && method.Name.Contains(">b__");

        static Type? OwnerType(MethodBase method)
        {
            var type = method.DeclaringType;
            while (type != null && type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null) type = type.DeclaringType;
            return type;
        }

        static bool IsSkipped(MethodBase method, ICollection<string>? skip)
        {
            // Compiler-generated closures and state machines belong to the method that declared them.
            var type = OwnerType(method);
            var name = type?.FullName ?? "";
            foreach (var prefix in SkippedPrefixes)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            if (skip != null)
                foreach (var prefix in skip)
                    if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal)) return true;
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
