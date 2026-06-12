using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.hello")]
public class HelloWorldHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.hello";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        var data = new
        {
            message = "Hello from the ExamplePlugin!",
            args = request.Args ?? new(),
            plugin = "ExamplePlugin"
        };
        return Task.FromResult(new IpcResponse(true, null, data));
    }
}
