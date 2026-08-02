using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace ScarletRadioControl.Web.Formatters;

public sealed class SdpInputFormatter : TextInputFormatter
{

	public SdpInputFormatter()
	{
		this.SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/sdp"));
		this.SupportedEncodings.Add(Encoding.UTF8);
	}

	protected override bool CanReadType(Type type) => type == typeof(string);

	public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
	{
		using var streamReader = new StreamReader(context.HttpContext.Request.Body,encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
		var sdp = await streamReader.ReadToEndAsync(context.HttpContext.RequestAborted);
		return await InputFormatterResult.SuccessAsync(sdp);
	}

}
