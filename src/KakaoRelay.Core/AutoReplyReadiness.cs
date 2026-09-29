namespace KakaoRelay.Core;

public static class AutoReplyReadiness
{
    public static async Task<(List<LocalRoom> Rooms, List<LocalRoom> Selected)> PrepareAsync(
        IChatReader reader, IReadOnlyList<LocalRoom> selected, Func<List<ConversationTarget>> scan,
        CancellationToken cancellation = default)
    {
        if (selected.Count is < 1 or > 3) throw new InvalidOperationException("자동 답장할 방을 1~3개 선택하세요.");
        var rooms = await reader.RoomsAsync(cancellation);
        var ready = new List<LocalRoom>();
        foreach (var old in selected)
        {
            cancellation.ThrowIfCancellationRequested();
            var room = rooms.SingleOrDefault(r => r.Profile == old.Profile && r.Id == old.Id)
                ?? throw new InvalidOperationException($"{old.Title} · 현재 로컬 목록에서 이 방을 찾지 못했습니다. 카카오톡에서 방을 열어주세요.");
            try
            {
                // Readability in an earlier room list is only a snapshot, not a start prohibition.
                var context = await reader.ReadAsync(room.Profile, room.Id, 1, cancellation);
                room = context.Room;
            }
            catch (ApiFailure error)
            {
                throw new InvalidOperationException($"{room.Title} · 대화 읽기 확인 실패: {error.Message}", error);
            }
            if (!room.Readable) throw new InvalidOperationException($"{room.Title} · 대화 읽기를 확인하지 못했습니다. 방을 연 뒤 다시 시작하세요.");
            var index = rooms.FindIndex(r => r.Profile == room.Profile && r.Id == room.Id);
            rooms[index] = room;
            ready.Add(room);
        }
        var windows = await Task.Run(scan, cancellation);
        foreach (var room in ready)
        {
            if (rooms.Count(r => r.Title == room.Title) != 1)
                throw new InvalidOperationException($"{room.Title} · 로컬 목록에 같은 제목의 방이 여러 개 있어 발송 대상을 구분할 수 없습니다. 방 이름을 다르게 설정하세요.");
            var matches = windows.Where(w => w.Title == room.Title).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"{room.Title} · 대화는 읽을 수 있지만 발송할 열린 창이 없습니다. 카카오톡에서 방을 별도 창으로 열고 최소화를 해제하세요.");
            if (matches.Count != 1 || !matches[0].Selectable)
                throw new InvalidOperationException($"{room.Title} · 같은 제목의 열린 창이 여러 개여서 발송할 창을 구분할 수 없습니다.");
        }
        return (rooms, ready);
    }
}
