using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

internal readonly record struct IconJob(string Path,string Key,int Pixels,bool Fast);

// 图标后台管线：缓存与解码队列都按「驻留后台时尽量归零」设计。
//
// 旧实现有两个长期占用：静态构造即启动两条 STA 线程并永久阻塞在
// BlockingCollection 上；ConcurrentDictionary 图标缓存没有任何上限，
// 跨屏 DPI、窗口缩放、图标库滚动会不断写入新尺寸键，进程越用越胖。
// 这里改成：解码线程惰性启动、空闲自动退出；缓存按位图字节预算做 LRU 淘汰。
internal sealed class IconCache
{
    internal const long DefaultBudget = 48L*1024*1024;
    private sealed class Entry
    {
        public required string Key {get;init;}
        public required ImageSource Image {get;set;}
        public required long Bytes {get;set;}
    }
    private readonly object _gate=new();
    private readonly Dictionary<string,LinkedListNode<Entry>> _map=new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru=new();
    private long _bytes;
    private long _budget=DefaultBudget;

    internal long Budget {get{lock(_gate)return _budget;}}
    internal long Bytes {get{lock(_gate)return _bytes;}}
    internal int Count {get{lock(_gate)return _map.Count;}}

    // 只用于自检：把预算临时调小，避免为了触发淘汰而真的解码几百张位图。
    internal void SetBudget(long bytes){lock(_gate){_budget=Math.Max(4096,bytes);Trim();}}

    internal bool TryGet(string key,out ImageSource image)
    {
        lock(_gate)
        {
            if(!_map.TryGetValue(key,out var node)){image=null!;return false;}
            _lru.Remove(node);_lru.AddFirst(node);
            image=node.Value.Image;return true;
        }
    }

    internal void Add(string key,ImageSource image)
    {
        var bytes=EstimateBytes(image);
        lock(_gate)
        {
            if(_map.TryGetValue(key,out var existing))
            {
                _bytes-=existing.Value.Bytes;
                existing.Value.Bytes=bytes;existing.Value.Image=image;_bytes+=bytes;
                _lru.Remove(existing);_lru.AddFirst(existing);
            }
            else
            {
                var node=_lru.AddFirst(new Entry{Key=key,Image=image,Bytes=bytes});
                _map[key]=node;_bytes+=bytes;
            }
            Trim();
        }
    }

    // 面板收起时主动收缩到预算的一部分：UI 已经持有当前显示图标的 Source 引用，
    // 淘汰缓存条目不会让画面变空，只是下次 Refresh 时对冷门尺寸重新解码。
    internal void TrimForIdle(double fraction=.35)
    {
        lock(_gate)
        {
            var target=(long)(_budget*Math.Clamp(fraction,0,1));
            while(_bytes>target&&_lru.Last is { } node)Remove(node);
        }
    }

    internal void Clear()
    {
        lock(_gate){while(_lru.First is { } node)Remove(node);}
    }

    private void Trim()
    {
        // 始终保留最近使用的一条，避免单张超大位图被立刻淘汰造成反复解码。
        while(_bytes>_budget&&_lru.Count>1&&_lru.Last is { } node)Remove(node);
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);_map.Remove(node.Value.Key);_bytes-=node.Value.Bytes;
        node.Value.Image=null!;
    }

    private static long EstimateBytes(ImageSource image)
    {
        try
        {
            if(image is BitmapSource bitmap)
                return Math.Max(1024L,(long)bitmap.PixelWidth*bitmap.PixelHeight*Math.Max(1,bitmap.Format.BitsPerPixel/8));
        }
        catch{/* 非常规画刷按保守值计入 */}
        return 64*1024L;
    }
}

// 惰性 STA 解码池。Shell 图标提取必须运行在 STA 单元，因此不能用普通线程池；
// 但线程无需常驻：空闲超过 IdleTimeout 后自行退出，下一批请求再按需拉起。
internal sealed class IconJobQueue
{
    private const int MaxWorkers=2;
    private readonly Action<IconJob> _process;
    private readonly object _gate=new();
    private readonly Queue<IconJob> _jobs=new();
    private readonly SemaphoreSlim _signal=new(0);
    private TimeSpan _idle=TimeSpan.FromSeconds(20);
    private int _workers;
    private int _active;
    private long _processed;

    internal IconJobQueue(Action<IconJob> process)=>_process=process;

    internal int ActiveWorkers=>Volatile.Read(ref _active);
    internal int PendingCount{get{lock(_gate)return _jobs.Count;}}
    internal long ProcessedCount=>Interlocked.Read(ref _processed);
    internal TimeSpan IdleTimeout{get{lock(_gate)return _idle;}}
    // 只用于自检：缩短空闲窗口，无需真的等待 20 秒。
    internal void SetIdleTimeout(TimeSpan value){lock(_gate)_idle=value;}

    internal void Enqueue(IconJob job)
    {
        Thread? starter=null;
        lock(_gate)
        {
            _jobs.Enqueue(job);
            if(_workers<MaxWorkers)
            {
                _workers++;
                starter=new Thread(WorkerLoop){IsBackground=true,Name="DesktopNook shell icons "+(_workers)};
                starter.SetApartmentState(ApartmentState.STA);
            }
        }
        starter?.Start();
        _signal.Release();
    }

    private void WorkerLoop()
    {
        Interlocked.Increment(ref _active);
        try
        {
            while(true)
            {
                TimeSpan idle;lock(_gate)idle=_idle;
                if(!_signal.Wait(idle))
                {
                    // 超时后必须在同一把锁下复核队列：若恰好有新任务入队，
                    // 不能让最后一个 worker 退出而把任务留在队列里饿死。
                    lock(_gate)
                    {
                        if(_jobs.Count==0){_workers--;return;}
                    }
                    continue;
                }
                IconJob job;
                lock(_gate)
                {
                    if(_jobs.Count==0)continue;
                    job=_jobs.Dequeue();
                }
                try{_process(job);}
                catch{/* 单个图标失败不影响队列，也不让 worker 退出 */}
                finally{Interlocked.Increment(ref _processed);}
            }
        }
        finally{Interlocked.Decrement(ref _active);}
    }
}
