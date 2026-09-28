using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public partial class PersonaImagesPanel : UserControl, INotifyPropertyChanged
{
    public sealed record VersionChoice(string Id, string Name);
    public sealed record ImageChoice(PersonaImage Asset, ImageSource Thumbnail, string Label);
    private Func<AiSettings>? getSettings;
    private Func<PersonaProfile>? getPersona;
    private Func<bool>? savePersona;
    private PersonaImageCatalog? catalog;
    private PersonaImageLibrary library = new();
    private CancellationTokenSource? operation;
    private VersionChoice provider = new("astra", "GPT · Codex CLI");
    private ImageChoice? selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ImageSource?>? AvatarChanged;
    public IReadOnlyList<VersionChoice> Providers { get; } = [new("astra", "GPT · Codex CLI"), new("grok", "Grok CLI")];
    public IReadOnlyList<PersonaExpression> Expressions => PersonaExpression.All;
    public ObservableCollection<ImageChoice> Images { get; } = [];
    public string GptModel { get => getSettings?.Invoke().ImageGptModel ?? PersonaImageCliGenerator.GptModel; set { if (getSettings is not null) getSettings().ImageGptModel = value; } }
    public string GrokModel { get => getSettings?.Invoke().ImageGrokModel ?? PersonaImageCliGenerator.GrokModel; set { if (getSettings is not null) getSettings().ImageGrokModel = value; } }
    public string VisualDirection { get; set; } = "작은 채팅 이미지에서도 표정이 잘 보이는 개성 있는 캐릭터";
    private double magentaTolerance = 30;
    public double MagentaTolerance { get => magentaTolerance; set { magentaTolerance = value; Changed(); } }
    public VersionChoice SelectedProvider { get => provider; set { if (value is null || Busy) return; provider = value; Reload(); } }
    public PersonaExpression SelectedExpression { get; set; } = PersonaExpression.All[1];
    public ImageChoice? SelectedImage { get => selected; set { selected = value; SelectedPreview = value?.Thumbnail; Changed(); } }
    public ImageSource? ReferencePreview { get; private set; }
    public ImageSource? SelectedPreview { get; private set; }
    public string SelectedDetail => selected is null ? "후보를 선택해 비교하세요." : $"{selected.Asset.Display} · {selected.Asset.Model}";
    public bool HasPrompt => !string.IsNullOrWhiteSpace(selected?.Asset.Prompt);
    public string SelectedPrompt => HasPrompt ? selected!.Asset.Prompt : "저장된 프롬프트 없음";
    public string AppliedLabel { get; private set; } = "적용된 이미지 없음";
    public string Status { get; private set; } = "페르소나를 선택하고 기준 이미지를 만들어 주세요.";
    public bool Busy => operation is not null;
    public bool Idle => !Busy && catalog is not null;
    public bool HasReference => library.ReferenceId is not null;
    public bool HasSelection => selected is not null;
    public Visibility ProgressVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;
    public PersonaImagesPanel() { InitializeComponent(); DataContext = this; }
    public void Initialize(Func<PersonaProfile> persona, Func<bool> save, Func<AiSettings> settings) { getPersona = persona; savePersona = save; getSettings = settings; SwitchPersona(); }
    public void SwitchPersona()
    {
        if (Busy || getPersona is null) return;
        catalog = new(AiSettings.ImageRoot(getPersona().Id)); Reload();
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    private PersonaImageStore Store => catalog!.Store(provider.Id);
    private void Reload(string? selectedId = null)
    {
        if (catalog is null) return;
        try
        {
            Store.RepairLegacyTransparentReferences(MagentaPng.IsTransparentCopy);
            library = Store.Load(); Images.Clear();
            foreach (var image in library.Images.OrderByDescending(i => i.CreatedAt))
            {
                var thumbnail = Preview(Store.ImagePath(image.Id));
                Images.Add(new(image, thumbnail, (library.Active.ContainsValue(image.Id) ? "✓ " : "") + image.Display));
            }
            ReferencePreview = library.ReferenceId is null ? null : Preview(Store.ImagePath(library.ReferenceId));
            SelectedImage = Images.FirstOrDefault(i => i.Asset.Id == selectedId) ?? Images.FirstOrDefault();
            var applied = catalog.AppliedProvider();
            AppliedLabel = applied is null ? "적용된 이미지 없음" : $"현재 적용: {getPersona!().Label} · {(applied == "astra" ? "GPT" : "Grok")}";
            var path = catalog.AppliedImagePath();
            AvatarChanged?.Invoke(path is null ? null : Preview(path));
        }
        catch (Exception e) { Status = "이미지 목록 확인 필요: " + e.Message; }
        Changed();
    }
    private static BitmapImage Preview(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 320; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    internal static byte[] NormalizePng(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 32_000_000) throw new InvalidDataException("이미지 해상도가 너무 큽니다.");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private async Task Generate(bool reference, bool missing)
    {
        if (!Idle || savePersona?.Invoke() != true) return;
        var persona = getPersona!();
        var model = provider.Id == "astra" ? GptModel : GrokModel;
        var id = provider.Id == "astra" ? "codex" : "grok";
        var configured = getSettings!().Providers.Single(p => p.Id == id);
        var generator = new PersonaImageCliGenerator(new AiProviderSettings { Id = id, Model = model, Effort = "low", Executable = configured.Executable }, NormalizePng);
        var service = new PersonaImageService(Store);
        var expressions = reference ? new[] { PersonaExpression.All[0] } : missing ? Expressions.Where(e => !library.Active.ContainsKey(e.Id)).ToArray() : [SelectedExpression];
        if (expressions.Length == 0) { Status = "모든 감정 이미지가 있습니다. 바꿀 감정을 선택해 재생성하세요."; Changed(); return; }
        using var cancellation = new CancellationTokenSource(); operation = cancellation; Changed();
        string? last = null;
        try
        {
            for (var i = 0; i < expressions.Length; i++)
            {
                Status = $"{provider.Name} · {(reference ? "기준 이미지" : expressions[i].Name)} 생성 중 ({i + 1}/{expressions.Length})"; Changed();
                var image = await service.GenerateAsync(persona.Persona, expressions[i].Id, reference, VisualDirection, provider.Id,
                    model + (provider.Id == "astra" ? " / imagegen" : " / image_gen"), generator, cancellation.Token);
                last = image.Id; Reload(last);
            }
            Status = "생성 완료 · 후보를 확인하고 ‘선택 이미지 채택’, ‘이 버전 적용’을 눌러주세요.";
        }
        catch (OperationCanceledException) { Status = "생성을 중지했습니다. 이미 완료한 이미지와 기존 선택은 보관됩니다."; }
        catch (Exception e) { Status = "생성 실패: " + e.Message; }
        finally { operation = null; Reload(last); Changed(); }
    }
    private async void GenerateReference_Click(object sender, RoutedEventArgs e) => await Generate(true, false);
    private async void GenerateExpression_Click(object sender, RoutedEventArgs e) => await Generate(false, false);
    private async void GenerateMissing_Click(object sender, RoutedEventArgs e) => await Generate(false, true);
    private async void MakeTransparent_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || selected is null) return;
        var store = Store;
        var sourceId = selected.Asset.Id;
        var tolerance = (int)Math.Round(MagentaTolerance);
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        Status = "선택한 이미지의 마젠타를 투명화하는 중…"; Changed();
        string? savedId = sourceId;
        try
        {
            var result = await Task.Run(() => MagentaPng.Convert(store.Read(sourceId), tolerance, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.ChangedPixels == 0) Status = "투명화할 마젠타가 없습니다. 허용 오차를 조절해 보세요.";
            else
            {
                savedId = store.AddTransparentCopy(sourceId, result.Png).Id;
                Status = $"{result.ChangedPixels:N0}개 픽셀 투명화 · 별도 PNG 후보로 저장했습니다. 사용하려면 ‘선택 이미지 채택’을 누르세요.";
            }
        }
        catch (OperationCanceledException) { Status = "투명화를 중지했습니다."; }
        catch (Exception error) { Status = "투명화 실패: " + error.Message; }
        finally { operation = null; Reload(savedId); Changed(); }
    }
    private void Manage(Action action)
    {
        if (!Idle) return;
        try { action(); Reload(selected?.Asset.Id); }
        catch (Exception e) { Status = e.Message; Changed(); }
    }
    private void UseImage_Click(object sender, RoutedEventArgs e) => Manage(() => { if (selected is null) return; Store.Use(selected.Asset.Id); Status = "선택한 이미지를 이 버전에 채택했습니다."; });
    private void ApplyVersion_Click(object sender, RoutedEventArgs e) => Manage(() => { catalog!.Apply(provider.Id); Status = provider.Name + "을 페르소나에 적용했습니다."; });
    private void DeleteImage_Click(object sender, RoutedEventArgs e) => Manage(() => { if (selected is null) return; Store.Delete(selected.Asset.Id); Status = "선택한 후보를 삭제했습니다."; });
    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPrompt) return;
        try { Clipboard.SetText(selected!.Asset.Prompt); Status = "생성 프롬프트를 복사했습니다."; }
        catch (Exception error) { Status = "복사 실패: " + error.Message; }
        Changed();
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Manage(() => { Directory.CreateDirectory(Store.Root); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Store.Root) { UseShellExecute = true }); });
    public void Stop() => operation?.Cancel();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Stop();
}
