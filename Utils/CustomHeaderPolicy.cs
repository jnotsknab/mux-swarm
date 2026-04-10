using System.ClientModel.Primitives;

namespace MuxSwarm.Utils;

sealed class CustomHeaderPolicy(Dictionary<string, string> headers)
    : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        foreach (var kvp in headers)
            message.Request.Headers.Set(kvp.Key, kvp.Value);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        foreach (var kvp in headers)
            message.Request.Headers.Set(kvp.Key, kvp.Value);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }
}