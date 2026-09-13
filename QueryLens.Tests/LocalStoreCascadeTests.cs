using QueryLens.Core;
namespace QueryLens.Tests;
public sealed class LocalStoreCascadeTests
{
 [Fact]
 public async Task DeleteConnectionRemovesDependentData()
 {
  var path=Path.Combine(Path.GetTempPath(),$"ql-cascade-{Guid.NewGuid():N}.db");
  try { var id=Guid.NewGuid(); await using(var s=new LocalStore(path)){await s.InitializeAsync(); var p=new ConnectionProfile(id,"x",DatabaseDialect.MySql,"h",3306,"d"); await s.SaveConnectionAsync(p); var f=SqlFingerprinter.Fingerprint("select 1",p.Dialect); await s.SaveSlowQueryAsync(new SlowQuery(Guid.NewGuid(),id,"select 1",f.RedactedSql,f.NormalizedSql,f.Fingerprint,DateTimeOffset.UtcNow,1,null,null,null,null,null,"d",false,p.Dialect)); await s.SavePlanAsync(PlanParser.ParseJson("{\"Node Type\":\"Seq Scan\"}",p.Dialect) with {ConnectionId=id}); await s.DeleteConnectionAsync(id); Assert.Empty(await s.GetConnectionsAsync()); Assert.Empty(await s.GetSlowQueriesAsync(id)); Assert.Empty(await s.GetPlansAsync(id)); }
  } finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(f)) File.Delete(f); }
 }
 [Fact]
 public async Task SetPlanBaselineResetsOnlyMatchingGroup()
 {
  var path=Path.Combine(Path.GetTempPath(),$"ql-base-{Guid.NewGuid():N}.db"); try { await using var s=new LocalStore(path); await s.InitializeAsync(); var cid=Guid.NewGuid(); var p=new ConnectionProfile(cid,"x",DatabaseDialect.PostgreSql,"h",1,"d"); await s.SaveConnectionAsync(p); var a=PlanParser.ParseJson("{\"Node Type\":\"Seq Scan\"}",p.Dialect) with {ConnectionId=cid,QueryFingerprint="q",IsBaseline=true}; var b=PlanParser.ParseJson("{\"Node Type\":\"Index Scan\"}",p.Dialect) with {ConnectionId=cid,QueryFingerprint="q"}; var c=PlanParser.ParseJson("{\"Node Type\":\"Sort\"}",p.Dialect) with {ConnectionId=cid,QueryFingerprint="other",IsBaseline=true}; await s.SavePlanAsync(a); await s.SavePlanAsync(b); await s.SavePlanAsync(c); await s.SetPlanBaselineAsync(b.Id); var plans=await s.GetPlansAsync(cid); Assert.True(plans.Single(x=>x.Id==b.Id).IsBaseline); Assert.False(plans.Single(x=>x.Id==a.Id).IsBaseline); Assert.True(plans.Single(x=>x.Id==c.Id).IsBaseline); } finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(f)) File.Delete(f); } }
}
