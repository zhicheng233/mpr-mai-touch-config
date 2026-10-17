using Xunit;

// Localizer.Instance 是进程级可名单例，语言是可变的全局状态：
// 切语言（SetLanguage）会让其他测试同时读到的字符串变成另一门语言。
// 因此本程序集不允许并行跑测试集合。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
