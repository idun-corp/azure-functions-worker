using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.Azure.Functions.Worker;

public static class HttpExtensions
{
    public static FunctionContext? GetFunctionContext(this HttpContext httpContext)
    {
        return httpContext.Features.Get<FunctionContext>();
    }

    public static ActionContext? GetActionContext(this HttpContext httpContext)
    {
        return httpContext.Features.Get<ActionContext>();
    }

    internal static void SetActionContext(this HttpContext httpContext, ActionContext actionContext)
    {
        httpContext.Features.Set(actionContext);
    }
}
