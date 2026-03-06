using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace MuxSwarm;

public class McpAITool : AITool
{
    private readonly string _name;
    private readonly string _description;
    private readonly Func<string, Dictionary<string, object>, Task<object>> _invokeFunc;
    
    public McpAITool(
        string name, 
        string description, 
        Func<string, Dictionary<string, object>, Task<object>> invokeFunc)
    {
        _name = name;
        _description = description;
        _invokeFunc = invokeFunc;
    }
    
    public override string Name => _name;
    
    public override string Description => _description;
    
    // The actual invocation will depend on how the framework calls your tool
    // You might need to implement additional methods or interfaces
    public Task<object> InvokeAsync(Dictionary<string, object?> arguments)
    {
        var cleanArgs = arguments
            .Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
            
        return _invokeFunc(_name, cleanArgs);
    }
}