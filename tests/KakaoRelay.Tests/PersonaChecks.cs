using KakaoRelay.Core;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class PersonaChecks
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII=");
    private sealed class ImageGenerator : IPersonaImageGenerator
    {
        public List<byte[]?> References = [];
        public List<string> Prompts = [];
        public bool Fail;
        public Task<byte[]> GenerateAsync(string prompt, byte[]? reference, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); References.Add(reference); Prompts.Add(prompt); if (Fail) throw new IOException("fixture failure"); return Task.FromResult(Png); }
    }
    private sealed class Transport : IImageSendTransport
    {
        public int Attachments;
        public bool Fail;
        public string? AttachedPath;
        public void Validate(TestSendRequest request) { }
        public void Attach(string path) { Attachments++; AttachedPath = path; if (Fail) throw new IOException("uncertain attachment"); }
        public bool ConfirmPreview() => true;
    }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var settingsStore = new AiSettingsStore(Path.Combine(root, "personas", "settings.json"));
        var settings = new AiSettings { Persona = new() { Name = "기존 봇" } }; settingsStore.Save(settings);
        settings = settingsStore.Load();
        check(settings.Personas.Count == 3 && settings.Personas[0].Persona.Name == "기존 봇", "Legacy persona migrates into three slots without changing its personality");
        settings.RoomPersonas[AiSettings.RoomKey("profile-a", "100")] = settings.Personas[1].Id;
        settings.RoomPersonas[AiSettings.RoomKey("profile-b", "100")] = settings.Personas[2].Id;
        settingsStore.Save(settings); settings = settingsStore.Load();
        check(settings.ResolvePersona("profile-a", "100").Id == "work" && settings.ResolvePersona("profile-b", "100").Id == "friend" && settings.ResolvePersona("profile-a", "101").Id == "default", "Room mappings persist and separate equal room IDs across profiles with a default fallback");
        settings.Trigger = "arbitrary-trigger";
        check(settings.TriggerForRoom("profile-a", "100") == "@" + settings.Personas[1].Persona.Name && settings.Trigger == "@" + settings.Persona.Name, "Mention is forced to the connected bot name; custom legacy triggers cannot override it");
        settings.Personas[1].Persona.Name = "새봇";
        check(settings.TriggerForRoom("profile-a", "100") == "@새봇", "Renaming the connected persona updates its room mention");
        var mentions = new[] { new LocalMessage("1", "other", "", DateTimeOffset.Now, 1, "@새봇 hello", false), new LocalMessage("2", "other", "", DateTimeOffset.Now, 1, "@새봇다른봇 hello", false) };
        check(AutoReplySession.Candidates(mentions, "0", "self", "@새봇").Select(m => m.Id).SequenceEqual(["1"]), "A longer bot name is not mistaken for this bot's mention");
        check(AiService.ParseReply("답변\n[[emotion:happy]]") == ("답변", "happy") && AiService.ParseReply("일반 답변") == ("일반 답변", "neutral"), "Emotion metadata is removed from sent text and missing metadata falls back to neutral");
        var catalog = new PersonaImageCatalog(Path.Combine(root, "images")); var store = catalog.Store("astra"); var service = new PersonaImageService(store); var generator = new ImageGenerator();
        var basis = await service.GenerateAsync(settings.Persona, "neutral", true, "simple", "astra", "fixture", generator, default);
        check(File.ReadAllText(Path.Combine(store.Root, "library.json")).Contains(basis.PersonaName), "Image library metadata stores Korean persona names directly");
        var happy = await service.GenerateAsync(settings.Persona, "happy", false, "", "astra", "fixture", generator, default);
        var another = await service.GenerateAsync(settings.Persona, "happy", false, "", "astra", "fixture", generator, default);
        var reloadedImages = new PersonaImageStore(store.Root).Load().Images;
        check(new[] { basis, happy, another }.Select(i => reloadedImages.Single(saved => saved.Id == i.Id).Prompt).SequenceEqual(generator.Prompts), "Each image persists the exact generation prompt across library reloads");
        check(generator.References[0] is null && generator.References.Skip(1).All(b => b!.SequenceEqual(Png)) && happy.ReferenceId == basis.Id && another.ReferenceId == basis.Id, "Every expression and regeneration reuses the original reference bytes");
        check(store.Load().Active["happy"] == happy.Id && store.Load().Images.Count == 3, "Regeneration keeps the approved image and saves a separate candidate");
        store.Use(another.Id); catalog.Apply("astra");
        check(catalog.AppliedImagePath("happy") == store.ImagePath(another.Id), "Applying a candidate changes only the chosen expression");
        check(catalog.ResolveAttachment("default", "happy") == new PersonaAttachment("default", "astra", another.Id)
            && catalog.ResolveAttachment("default", "sad") is null, "Reply attachments select the adopted emotion image and omit missing expressions instead of sending neutral");
        foreach (var emotion in PersonaExpression.All)
        {
            var parsed = AiService.ParseReply($"감정 답변\n[[emotion:{emotion.Id}]]");
            check(parsed == ("감정 답변", emotion.Id), $"Reply emotion {emotion.Id} is extracted without leaking the marker into the text");
        }
        generator.Fail = true;
        try { await service.GenerateAsync(settings.Persona, "sad", false, "", "astra", "fixture", generator, default); check(false, "failure fixture"); } catch (IOException) { }
        check(store.Load().Images.Count == 3 && store.Load().Active["happy"] == another.Id, "Provider failure preserves the existing library and selection");
        generator.Fail = false;
        var nextBasis = await service.GenerateAsync(settings.Persona, "neutral", true, "new", "astra", "fixture", generator, default);
        check(store.Load().ReferenceId == basis.Id, "New reference remains a candidate until explicitly adopted");
        store.Use(nextBasis.Id);
        check(store.Load().Active.Count == 1 && catalog.AppliedImagePath("sad") == store.ImagePath(nextBasis.Id), "Changing reference clears old expression selections and missing emotions use the new neutral reference");
        try { store.Use(happy.Id); check(false, "cross-reference expression"); } catch (InvalidOperationException) { check(true, "Expressions from different characters cannot be mixed"); }
        var grok = catalog.Store("grok"); grok.Add("neutral", null, "other", "fixture", "grok", "fixture", Png); catalog.Apply("grok");
        check(catalog.AppliedProvider() == "grok" && store.Load().Images.Count == 4 && grok.Load().Images.Count == 1, "Astra and Grok sets remain independent when switching applied version");
        check(catalog.ResolveAttachment("friend", "happy") is null
            && catalog.ResolveAttachment("friend", "neutral") is { PersonaId: "friend", Provider: "grok" } attachment && attachment.ImageId == grok.Load().ReferenceId, "Neutral greeting uses the applied provider; missing emotions do not substitute its default image");
        try { store.Delete(nextBasis.Id); check(false, "active deletion"); } catch (InvalidOperationException) { check(true, "Active reference cannot be deleted"); }
        try { store.ImagePath("../outside"); check(false, "path traversal"); } catch (InvalidDataException) { check(true, "Image IDs cannot escape the managed library"); }
        await ImageCliChecks.RunAsync(root, check);
        byte[] pixels = [255, 0, 255, 255, 240, 15, 245, 128, 0, 0, 255, 255, 255, 255, 255, 255, 255, 0, 255, 0];
        var exact = pixels.ToArray();
        check(MagentaTransparency.Apply(exact, 0) == 1 && exact.Take(4).All(b => b == 0) && exact.Skip(4).SequenceEqual(pixels.Skip(4)), "Exact magenta removal preserves near colors, existing transparency and other pixels");
        check(MagentaTransparency.Apply(pixels, 30) == 2 && pixels.Take(8).All(b => b == 0) && pixels[11] == 255 && pixels[15] == 255, "Magenta tolerance removes near-magenta including partial alpha while preserving red and white");
        var original = store.Read(happy.Id);
        var beforeCopy = store.Load();
        var transparent = store.AddTransparentCopy(happy.Id, Png);
        var transparentBasis = store.AddTransparentCopy(nextBasis.Id, Png);
        var afterCopy = store.Load();
        check(afterCopy.Images.Single(i => i.Id == transparent.Id).Prompt == happy.Prompt && afterCopy.Images.Single(i => i.Id == transparentBasis.Id).Prompt == nextBasis.Prompt, "Transparent copies preserve their original image generation prompts");
        check(transparent.Id != happy.Id && transparent.ReferenceId == happy.ReferenceId && transparent.Expression == happy.Expression && afterCopy.Images.Single(i => i.Id == transparent.Id).IsTransparentCopy && store.Read(happy.Id).SequenceEqual(original), "Transparent candidate persists its expression and reference without overwriting the original");
        check(transparentBasis.IsReference && afterCopy.ReferenceId == beforeCopy.ReferenceId && afterCopy.Active.OrderBy(p => p.Key).SequenceEqual(beforeCopy.Active.OrderBy(p => p.Key)), "Saving transparent reference and expression candidates leaves active selections unchanged");
        CheckTransparentFamilies(Path.Combine(root, "transparent-families"), check);
        var ledger = Path.Combine(root, "image-sends"); var imagePath = store.ImagePath(nextBasis.Id); var transport = new Transport();
        var request = new TestSendRequest("image-once", "fixture-room", ImageSender.FileIdentity(imagePath), 1, "0x1", DateTimeOffset.Now.AddMinutes(5), true);
        var transfers = Path.Combine(root, "transfers");
        var first = ImageSender.SendOnce(request, imagePath, transport, ledger, transfers); var duplicate = ImageSender.SendOnce(request, imagePath, transport, ledger, transfers);
        check(first.Kind == "image" && first.AttachmentQueued && first.EnterPosted && transport.Attachments == 1 && duplicate.RequestId == first.RequestId, "Duplicate image requests return the saved receipt without attaching again");
        check(transport.AttachedPath != imagePath && File.ReadAllBytes(transport.AttachedPath!).SequenceEqual(Png), "Attachment uses an independent byte-identical file that survives library edits");
        var failed = new Transport { Fail = true }; var failedRequest = request with { RequestId = "image-uncertain" };
        var uncertain = ImageSender.SendOnce(failedRequest, imagePath, failed, ledger, transfers);
        ImageSender.SendOnce(failedRequest, imagePath, failed, ledger, transfers);
        check(uncertain.Status == "needs-review" && failed.Attachments == 1, "Uncertain attachment is never replayed even when no confirmation was posted");
        settings.Persona.Name = "호환성"; settingsStore.Save(settings);
        check(settingsStore.Load().Personas[0].Persona.Name == "호환성", "Legacy persona edits update the first slot after migration");
        check(new ApiSendCommand("same", "room", "").Fingerprint() != new ApiSendCommand("same", "room", "") { Attachment = new("default", "astra", basis.Id) }.Fingerprint(), "Attachment identity participates in API idempotency");
    }
    private static void CheckTransparentFamilies(string root, Action<bool, string> check)
    {
        var store = new PersonaImageStore(root);
        var basis = store.Add("neutral", null, "fixture", "reference", "astra", "fixture", Png);
        var happy = store.Add("happy", basis.Id, "fixture", "expression", "astra", "fixture", Png);
        var transparentHappy = store.AddTransparentCopy(happy.Id, Png);
        store.Use(transparentHappy.Id);
        var transparentBasis = store.AddTransparentCopy(basis.Id, Png);
        store.Use(transparentBasis.Id);
        var loaded = new PersonaImageStore(root).Load();
        check(loaded.Active["happy"] == transparentHappy.Id && loaded.ReferenceId == transparentBasis.Id
            && loaded.Images.Single(i => i.Id == transparentBasis.Id).ReferenceFamilyId == basis.Id,
            "Adopting a transparent reference preserves approved expressions and family identity after restart");
        store.Use(happy.Id); store.Use(transparentHappy.Id);
        var sad = store.Add("sad", transparentBasis.Id, "fixture", "sad", "astra", "fixture", Png);
        var transparentSad = store.AddTransparentCopy(sad.Id, Png); store.Use(transparentSad.Id);
        var twice = store.AddTransparentCopy(transparentBasis.Id, Png); store.Use(twice.Id);
        store.Use(basis.Id);
        check(store.Load().Active["sad"] == transparentSad.Id && store.Load().Active["happy"] == transparentHappy.Id
            && twice.ReferenceFamilyId == basis.Id, "Original, transparent and repeatedly processed references accept expressions created from any member of the same family");
        store.Use(twice.Id);
        var different = store.Add("neutral", null, "fixture", "reference", "astra", "fixture", Png);
        store.Use(different.Id);
        check(store.Load().Active.Count == 1, "Regenerated references with identical prompts remain different characters");
        try { store.Use(transparentHappy.Id); check(false, "Cross-family transparent expression accepted"); }
        catch (InvalidOperationException) { check(true, "Transparent expressions from different characters remain rejected"); }
        var legacy = store.Add("neutral", null, "fixture", "reference", "astra", "fixture", Png, true);
        check(store.RepairLegacyTransparentReferences((_, _) => true) == 0, "Ambiguous legacy reference copies are not linked based on matching prompts");
        store.Delete(legacy.Id);
        var legacySource = store.Add("neutral", null, "fixture", "legacy unique", "astra", "fixture", Png);
        var legacyExpression = store.Add("happy", legacySource.Id, "fixture", "legacy expression", "astra", "fixture", Png);
        var legacyCopy = store.Add("neutral", null, "fixture", "legacy unique", "astra", "fixture", Png, true);
        store.Use(legacyCopy.Id);
        check(store.RepairLegacyTransparentReferences((_, _) => false) == 0, "Legacy repair requires matching pixels even when metadata is unique");
        check(store.RepairLegacyTransparentReferences((_, _) => true) == 1 && store.RepairLegacyTransparentReferences((_, _) => true) == 0,
            "Verified legacy reference repair persists and is idempotent");
        check(File.Exists(Path.Combine(root, "library.json.before-family-repair.bak")), "Legacy family repair keeps the pre-migration manifest backup");
        store.Use(legacyExpression.Id);
        check(store.Load().Active["happy"] == legacyExpression.Id, "Existing expressions can be adopted after repairing an already active legacy transparent reference");
        store.Use(different.Id); store.Delete(legacyExpression.Id);
        try { store.Delete(legacySource.Id); check(false, "Family root deletion accepted"); }
        catch (InvalidOperationException) { check(true, "A family root cannot be deleted while transparent references depend on it"); }
        byte[] pixels = [255, 0, 255, 255, 240, 15, 245, 128, 0, 0, 255, 255, 255, 255, 255, 255, 255, 0, 255, 0];
        var exact = pixels.ToArray(); MagentaTransparency.Apply(exact, 0);
        var near = pixels.ToArray(); MagentaTransparency.Apply(near, 30);
        check(MagentaTransparency.IsTransparentCopy(pixels, exact) && MagentaTransparency.IsTransparentCopy(pixels, near), "Legacy pixel matching recognizes exact and tolerance-based magenta removal");
        var wrong = near.ToArray(); wrong[8] = 1;
        var inconsistent = pixels.ToArray(); Array.Clear(inconsistent, 4, 4);
        check(!MagentaTransparency.IsTransparentCopy(pixels, wrong) && !MagentaTransparency.IsTransparentCopy(pixels, inconsistent)
            && !MagentaTransparency.IsTransparentCopy(pixels, pixels), "Legacy pixel matching rejects changed artwork, inconsistent thresholds and unchanged images");
    }
}

