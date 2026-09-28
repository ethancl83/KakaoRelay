using KakaoRelay.Core;
using System.Text.Json;

internal static class TestSendChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var ledger = Path.Combine(root, "send-ledger");
        TestSendRequest Request(string id) => new(id, "Test recipient", "Test body", 123, "0x123", DateTimeOffset.Now.AddMinutes(5));
        var transport = new FakeTransport();
        var request = Request("once");
        var receipt = TestSender.SendOnce(request, transport, ledger);
        check(transport.PostCount == 1 && receipt.Status == "needs-review" && receipt.InputCleared == true, "One Enter request remains unconfirmed even when input clears");
        TestSender.SendOnce(request, transport, ledger);
        check(transport.PostCount == 1 && transport.WriteCount == 1, "Repeated request never types or sends again");
        var conflictRejected = false;
        try { TestSender.SendOnce(request with { Message = "Other message" }, transport, ledger); } catch (InvalidOperationException) { conflictRejected = true; }
        check(conflictRejected && transport.PostCount == 1, "Changed body cannot reuse an earlier request ID");
        var draft = new FakeTransport { Draft = "Unsent user text" };
        var blocked = TestSender.SendOnce(Request("draft"), draft, ledger);
        check(blocked.Status == "blocked-before-input" && draft.WriteCount == 0 && draft.PostCount == 0 && draft.Draft == "Unsent user text", "Existing drafts remain untouched");
        var wrongTarget = new FakeTransport { RejectIdentity = true };
        TestSender.SendOnce(Request("identity"), wrongTarget, ledger);
        check(wrongTarget.WriteCount == 0 && wrongTarget.PostCount == 0, "Changed or ambiguous target prevents all input");
        var uncertain = new FakeTransport { ThrowAfterPosting = true };
        var failedRequest = Request("uncertain");
        var unknown = TestSender.SendOnce(failedRequest, uncertain, ledger);
        TestSender.SendOnce(failedRequest, uncertain, ledger);
        check(unknown.Status == "needs-review" && uncertain.PostCount == 1, "Uncertain Enter outcome never triggers retry");
        var interruptedRequest = Request("interrupted");
        File.WriteAllText(TestSender.ReceiptPath(ledger, interruptedRequest.RequestId), JsonSerializer.Serialize(new TestSendReceipt { RequestId = interruptedRequest.RequestId, Fingerprint = interruptedRequest.Fingerprint(), Status = "dispatching" }, ReportStore.JsonOptions));
        var afterCrash = new FakeTransport();
        TestSender.SendOnce(interruptedRequest, afterCrash, ledger);
        check(afterCrash.WriteCount == 0 && afterCrash.PostCount == 0, "A persisted dispatching request is never replayed after a crash");
        var expiredRejected = false;
        try { TestSender.SendOnce(Request("expired") with { ExpiresAt = DateTimeOffset.Now.AddSeconds(-1) }, new FakeTransport(), ledger); } catch (ArgumentException) { expiredRejected = true; }
        check(expiredRejected, "Expired target bindings cannot send");
        var cue = new FakeTransport { Draft = "메시지 입력" };
        TestSender.SendOnce(Request("unverified-cue"), cue, ledger);
        check(cue.PostCount == 0 && cue.WriteCount == 0, "Placeholder text is not treated as empty without visual verification");
        TestSender.SendOnce(Request("verified-cue") with { VerifiedEmptyPlaceholder = true }, cue, ledger);
        check(cue.PostCount == 1, "Visually verified gray placeholder can be replaced once");
        var cueWithDraft = new FakeTransport { Draft = "작성 중인 실제 초안" };
        TestSender.SendOnce(Request("cue-with-draft") with { VerifiedEmptyPlaceholder = true }, cueWithDraft, ledger);
        check(cueWithDraft.WriteCount == 0 && cueWithDraft.PostCount == 0, "Placeholder exception never accepts a different draft");
        var pendingRequest = Request("pending-draft");
        var pendingTransport = new FakeTransport { KeepDraftAfterPosting = true };
        TestSender.SendOnce(pendingRequest, pendingTransport, ledger);
        var continuationRequest = Request("continue-draft") with { ContinuationOf = pendingRequest.RequestId };
        var continuation = new FakeTransport { Draft = pendingRequest.Message };
        var continuationReceipt = TestSender.SendOnce(continuationRequest, continuation, ledger);
        check(continuation.PostCount == 1 && continuationReceipt.InputCleared == true, "Explicit continuation accepts only the prior identical pending draft");
        var secondContinuation = new FakeTransport { Draft = pendingRequest.Message };
        TestSender.SendOnce(Request("continue-again") with { ContinuationOf = pendingRequest.RequestId }, secondContinuation, ledger);
        check(secondContinuation.PostCount == 0 && secondContinuation.WriteCount == 0, "One reviewed predecessor cannot be continued twice");
        TestSender.RecordObserved(continuationRequest, ledger);
        var observedReceipt = JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(TestSender.ReceiptPath(ledger, continuationRequest.RequestId)), ReportStore.JsonOptions)!;
        check(observedReceipt.Status == "observed-in-chat", "Visual confirmation is recorded separately from Enter queueing");
    }

    private sealed class FakeTransport : ITestSendTransport
    {
        public string Draft { get; set; } = "";
        public bool RejectIdentity { get; set; }
        public bool ThrowAfterPosting { get; set; }
        public bool KeepDraftAfterPosting { get; set; }
        public int WriteCount { get; private set; }
        public int PostCount { get; private set; }
        public string ForegroundWindow => "0x999";
        public void ValidateTarget(TestSendRequest request) { if (RejectIdentity) throw new InvalidOperationException("Wrong target"); }
        public string ReadDraft() => Draft;
        public void WriteDraft(string message) { WriteCount++; Draft = message; }
        public void PostEnter() { PostCount++; if (!KeepDraftAfterPosting) Draft = ""; if (ThrowAfterPosting) throw new InvalidOperationException("Unknown outcome"); }
    }
}
