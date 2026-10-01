using System.Diagnostics;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Client.Tests;

// Every internal failure kind reaches callers as the same-named public kind. A member added on either side
// without a translation fails here as well as at compile time.
public sealed class FailureKindTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void SameName<TFrom, TTo>(Func<TFrom, TTo> map, params TFrom[] unmapped) where TFrom : struct, Enum where TTo : struct, Enum
    {
        foreach (var kind in Enum.GetValues<TFrom>())
        {
            if (unmapped.Contains(kind))
            {
                try { map(kind); throw new InvalidOperationException($"{typeof(TFrom).Name}.{kind} must not translate."); }
                catch (UnreachableException) { }
                continue;
            }
            Check(map(kind).ToString() == kind.ToString(), $"{typeof(TFrom).Name}.{kind} translated to {map(kind)}.");
        }
    }

    [Test]
    public void HttpKindsTranslateOneToOne()
    {
        SameName<OperationFailureKind, NativeHttpFailureKind>(NativeHttpException.KindOf);
        Check(Enum.GetValues<NativeHttpFailureKind>().Length == Enum.GetValues<OperationFailureKind>().Length, "Unreached public HTTP kind.");
    }

    [Test]
    public void OecpKindsTranslateExceptHttpOnlyKindsAndRpcErrors()
    {
        SameName<OperationFailureKind, NativeOecpFailureKind>(NativeOecpException.KindOf, OperationFailureKind.HttpStatus, OperationFailureKind.Redirect);
        SameName<NativeOecpFailureKind, OperationFailureKind>(OecpConnection.ConnectionInterrupted.KindOf, NativeOecpFailureKind.RpcError);
    }

    [Test]
    public void ObservationKindsTranslate() => SameName<ObservationFailureKind, NativeSubscriptionFailureKind>(NativeSubscriptionException.KindOf);
}
