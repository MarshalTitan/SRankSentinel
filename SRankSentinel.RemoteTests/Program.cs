using System.Text.Json;
using SRankSentinel;

var tests = new (string, Action)[]
{
    ("status is read-only and omits private binding ID", Status),
    ("skip and reset dispatch only their approved shared delegate", Dispatch),
    ("wrong character and off-framework calls cannot recover", Binding),
    ("disabled, loading, and already-recovering requests fail closed", Guards),
    ("skip without current fails; idle reset retains existing availability", Idle),
    ("exceptions do not cross IPC or expose exception messages", Exceptions),
    ("unsupported operations and disposed provider fail closed", Unsupported),
};
foreach (var (name, test) in tests) { test(); Console.WriteLine("PASS " + name); }
Console.WriteLine(tests.Length + "/" + tests.Length + " remote recovery contract tests passed");
static RecoverySnapshot Snapshot() => new("cid:0000000000000001", "Test Character", true, true, true,
    "SafeWait", "Holding at safe point", 1191, "Tyger", 2, false);
static JsonDocument Call(RemoteRecoveryController controller, RemoteRecoveryOperation operation,
    string key = "cid:0000000000000001", bool framework = true)
    => JsonDocument.Parse(controller.Handle(operation, key, framework));
static void Check(bool condition) { if (!condition) throw new Exception("Recovery contract assertion failed"); }
static void Status()
{
    var calls = 0; var snapshot = Snapshot();
    var controller = new RemoteRecoveryController(() => snapshot, () => calls++, () => calls++);
    using var result = Call(controller, RemoteRecoveryOperation.Status);
    Check(calls == 0 && result.RootElement.GetProperty("Success").GetBoolean());
    Check(!result.RootElement.GetRawText().Contains("cid:") && snapshot == Snapshot());
}
static void Dispatch()
{
    var skip = 0; var reset = 0; var snapshot = Snapshot();
    var controller = new RemoteRecoveryController(() => snapshot,
        () => { skip++; snapshot = snapshot with { State = "ResetToUldah" }; },
        () => { reset++; snapshot = snapshot with { QueueCount = 0 }; });
    using var a = Call(controller, RemoteRecoveryOperation.Skip);
    Check(skip == 1 && reset == 0 && a.RootElement.GetProperty("Status").GetProperty("QueueCount").GetInt32() == 2);
    using var b = Call(controller, RemoteRecoveryOperation.Reset);
    Check(skip == 1 && reset == 1 && b.RootElement.GetProperty("Status").GetProperty("QueueCount").GetInt32() == 0);
}
static void Binding()
{
    var calls = 0;
    var controller = new RemoteRecoveryController(Snapshot, () => calls++, () => calls++);
    using var a = Call(controller, RemoteRecoveryOperation.Skip, "cid:other");
    using var b = Call(controller, RemoteRecoveryOperation.Reset, framework: false);
    Check(calls == 0 && !a.RootElement.GetProperty("Success").GetBoolean() &&
        b.RootElement.GetProperty("Reason").GetString() == "FrameworkThreadRequired");
}
static void Guards()
{
    foreach (var snapshot in new[] { Snapshot() with { Enabled = false }, Snapshot() with { Ready = false },
        Snapshot() with { Recovering = true } })
    {
        var calls = 0;
        var controller = new RemoteRecoveryController(() => snapshot, () => calls++, () => calls++);
        foreach (var operation in new[] { RemoteRecoveryOperation.Skip, RemoteRecoveryOperation.Reset })
        {
            using var result = Call(controller, operation);
            Check(!result.RootElement.GetProperty("Success").GetBoolean() && calls == 0);
        }
    }
}
static void Idle()
{
    var calls = 0; var snapshot = Snapshot() with { ActiveHunt = null, State = "Idle" };
    var controller = new RemoteRecoveryController(() => snapshot, () => calls++, () => calls++);
    using var a = Call(controller, RemoteRecoveryOperation.Skip);
    Check(!a.RootElement.GetProperty("Success").GetBoolean() && calls == 0);
    using var b = Call(controller, RemoteRecoveryOperation.Reset);
    Check(b.RootElement.GetProperty("Success").GetBoolean() && calls == 1);
}
static void Exceptions()
{
    var controller = new RemoteRecoveryController(Snapshot,
        () => throw new Exception("fixture-private-secret"), () => throw new Exception("fixture-private-secret"));
    using var result = Call(controller, RemoteRecoveryOperation.Skip);
    Check(!result.RootElement.GetProperty("Success").GetBoolean() &&
        result.RootElement.GetProperty("Reason").GetString() == "RecoveryFailed" &&
        !result.RootElement.GetRawText().Contains("fixture-private-secret"));
    var brokenReader = new RemoteRecoveryController(() => throw new Exception("fixture-private-secret"), () => {}, () => {});
    using var readFailure = Call(brokenReader, RemoteRecoveryOperation.Status);
    Check(!readFailure.RootElement.GetProperty("Success").GetBoolean());
}
static void Unsupported()
{
    var calls = 0;
    var controller = new RemoteRecoveryController(Snapshot, () => calls++, () => calls++);
    using var a = Call(controller, (RemoteRecoveryOperation)99);
    var disposed = new RemoteRecoveryController(() => Snapshot() with { Loaded = false }, () => calls++, () => calls++);
    using var b = Call(disposed, RemoteRecoveryOperation.Reset);
    Check(calls == 0 && !a.RootElement.GetProperty("Success").GetBoolean() && !b.RootElement.GetProperty("Success").GetBoolean());
}
