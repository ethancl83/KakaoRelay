namespace KakaoRelay.Core;

public sealed class ApiFailure(int statusCode, string code, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
public sealed record LocalRoom(string Profile, string Id, string Title, bool Readable)
{
    public string Display => $"{Title} · {(Readable ? "읽기 가능" : "카카오톡에서 방을 열어주세요")}";
}
public sealed record LocalMessage(string Id, string AuthorId, string Author, DateTimeOffset Time, int Type, string Text, bool Deleted);
public sealed record ChatContext(LocalRoom Room, List<LocalMessage> Messages, DateTimeOffset ReadAt);
public interface IChatReader
{
    Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default);
    Task<ChatContext> ReadAsync(string profile, string roomId, int limit, CancellationToken cancellation = default);
}
