using System.Text;
using System.Text.Json;
using UsageLoom.Core;

static class ClaudeCodeTests
{
    private static void Check(bool value){if(!value)throw new Exception("Claude Code metadata assertion");}
    internal static void Run(Action<string,Action> test,string root)
    {
        string Line(string id="msg-a",string req="req-a",string uuid="row-a",int output=2,string at="2026-09-22T12:00:00Z",bool sidechain=false)=>JsonSerializer.Serialize(new{
            type="assistant",timestamp=at,requestId=req,uuid,sessionId="session-a",isSidechain=sidechain,
            message=new{id,model="claude-test",content=new[]{new{type="text",text="PRIVATE_SENTINEL",input="SECRET_BODY"}},usage=new{input_tokens=10,output_tokens=output,cache_read_input_tokens=20,cache_creation_input_tokens=30,cache_creation=new{ephemeral_5m_input_tokens=30}}}});
        ClaudeCodeUsage? Parse(string value)=>ClaudeCodeParser.Parse(Encoding.UTF8.GetBytes(value),"fixture-project","fixture-session");
        test("Claude Code：白名单解析，四类 Token 不重复计数，不保留正文",()=>
        {
            var row=Parse(Line())!;Check(row.Total==62&&row.Input==10&&row.CacheWrite==30);
            var metadata=JsonSerializer.Serialize(row);Check(!metadata.Contains("PRIVATE_SENTINEL")&&!metadata.Contains("SECRET_BODY")&&!metadata.Contains("session-a"));
            Check(row.Model=="claude-test"&&row.Session.Length==64);
        });
        test("Claude Code：回复身份去重而非 uuid，最新 usage 替换，跨文件副本与侧链重放",()=>
        {
            var first=Parse(Line())!;var last=Parse(Line(uuid:"row-b",output:20,at:"2026-09-22T12:00:01Z"))!;
            var rows=ClaudeCodeParser.Normalize([last,first,first,last]);Check(rows.Count==1&&rows[0].Total==80);
            Check(ClaudeCodeParser.Normalize([first,Parse(Line(id:"msg-b"))!]).Count==2);
            Check(ClaudeCodeParser.Normalize([first,Parse(Line(req:"req-b"))!]).Count==2);
            Check(ClaudeCodeParser.Normalize([first,Parse(Line(req:"side-replay",sidechain:true,output:999))!]).Single()==first);
            Check(ClaudeCodeParser.Normalize([Parse(Line(id:"agent-only",sidechain:true))!]).Count==1);
            Check(ClaudeCodeParser.Normalize([first,first with{Session="copy-session"}]).Count==1);
        });
        test("Claude Code：拒绝无身份、缺计数、负数、溢出、损坏、用户正文伪 usage",()=>
        {
            Check(Parse(Line(id:"")) is null);Check(Parse(Line(output:-1)) is null);
            Check(Parse(Line().Replace("\"output_tokens\":2","\"output_tokens\":9223372036854775807")) is null);
            Check(Parse(Line().Replace("\"input_tokens\":10,","")) is null);
            Check(Parse(Line().Replace("\"assistant\"","\"user\"")) is null);
            Check(Parse(Line()[..^1]) is null);Check(Parse(Line(at:"bad-date")) is null);
        });
        test("Claude Code：重读、追加半行恢复、重写缩短、删除与目录切换",()=>
        {
            var home=Path.Combine(root,"claude-code-scan");var project=Path.Combine(home,"projects","synthetic-project");Directory.CreateDirectory(project);
            var file=Path.Combine(project,"session.jsonl");File.WriteAllText(file,Line()+"\n",new UTF8Encoding(false));var reader=new ClaudeCodeReader();
            var first=reader.Read(home);Check(first.Status=="ready"&&first.Rows.Single().Total==62);
            Check(reader.Read(home).Rows.SequenceEqual(first.Rows));
            File.AppendAllText(file,Line(id:"msg-b")[..^1]);var partial=reader.Read(home);Check(partial.Status=="partial"&&partial.Rows.Count==1);
            File.AppendAllText(file,"}\n");Check(reader.Read(home).Rows.Count==2);
            File.WriteAllText(file,Line(output:3)+"\n");Check(reader.Read(home).Rows.Single().Output==3);
            File.Delete(file);Check(reader.Read(home).Status=="missing");
            Check(reader.Read(Path.Combine(root,"missing-code-home")).Rows.Count==0);
            Check(reader.Read("relative-directory").Status=="invalidPath");
        });
        test("Claude Code：子代理日志、跨文件去重和损坏记录保守提示",()=>
        {
            var home=Path.Combine(root,"claude-code-subagents");var project=Path.Combine(home,"projects","fixture");var agents=Path.Combine(project,"session","subagents");Directory.CreateDirectory(agents);
            File.WriteAllText(Path.Combine(project,"session.jsonl"),Line()+"\n");
            File.WriteAllText(Path.Combine(agents,"agent.jsonl"),Line()+"\n"+Line(id:"msg-agent",req:"agent-req",sidechain:true)+"\n{bad}\n");
            var result=new ClaudeCodeReader().Read(home);Check(result.Rows.Count==2&&result.Status=="partial"&&result.Skipped==1);
            var ignored=Path.Combine(project,"session","tool-results");Directory.CreateDirectory(ignored);File.WriteAllText(Path.Combine(ignored,"fake.jsonl"),Line(id:"not-a-transcript")+"\n");
            Check(new ClaudeCodeReader().Read(home).Rows.Count==2);
        });
        test("Claude Code：取消不发布半成品，超长行不影响下一条记录",()=>
        {
            var home=Path.Combine(root,"claude-code-bounds");var project=Path.Combine(home,"projects","fixture");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"session.jsonl"),new string('x',4*1024*1024+1)+"\n"+Line()+"\n");
            var reader=new ClaudeCodeReader();var result=reader.Read(home);Check(result.Rows.Count==1&&result.Skipped==1);
            using var cancel=new CancellationTokenSource();cancel.Cancel();var canceled=false;try{reader.Read(home,cancel.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled);
            Check(reader.Read(home).Rows.Count==1);
        });
    }
}
