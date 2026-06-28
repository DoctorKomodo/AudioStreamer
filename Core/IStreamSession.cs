namespace AudioStreamer
{
    /// <summary>A running streaming session (sender or receiver). Start is non-blocking; Stop tears down.</summary>
    public interface IStreamSession
    {
        bool IsRunning { get; }
        void Start();
        void Stop();
    }
}
