namespace EdgeLink.NetworkServer.Base;

// Console App 不需要主執行緒派發，保留介面以維持相容性
public interface IMainThreadDispatcher
{
    void Enqueue(Action action);
}

// 預設實作：直接執行（Console App 無主執行緒限制）
public class DirectDispatcher : IMainThreadDispatcher
{
    public static readonly DirectDispatcher Instance = new();
    public void Enqueue(Action action) => action?.Invoke();
}
