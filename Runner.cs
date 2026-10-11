using System;
using System.Data.SqlClient;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Collections.Generic;

public static class Program {
    [DllImport("ole32.dll")] static extern int CoInitializeSecurity(IntPtr sd,int count,IntPtr services,IntPtr reserved,uint auth,uint impersonation,IntPtr authList,uint capabilities,IntPtr reserved2);
    [DllImport("ole32.dll")] static extern int CreateStreamOnHGlobal(IntPtr memory,bool deleteOnRelease,out IStream stream);
    [DllImport("ole32.dll")] static extern int CoUnmarshalInterface(IStream stream,ref Guid iid,out IntPtr result);
    [DllImport("NativeHost.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] static extern int StartProbe(IntPtr server,string machine,uint pid);
    [DllImport("NativeHost.dll",CallingConvention=CallingConvention.Cdecl)] static extern int FinishProbe();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Inspector();
    [DllImport("NativeHost.dll",CallingConvention=CallingConvention.Cdecl)] static extern void SetInspector(Inspector callback);
    static Inspector inspector;
    static bool storageVerified;
    static void InspectStorage(SqlConnection connection,HashSet<int> baseline) {
        try {
            var candidates=new List<int>();
            using(var cmd=connection.CreateCommand()) {
                cmd.CommandTimeout=5;
                cmd.CommandText="SET LOCK_TIMEOUT 2000; SELECT t.object_id,t.name FROM tempdb.sys.tables t WHERE t.name LIKE '#%' AND EXISTS (SELECT 1 FROM tempdb.sys.columns c WHERE c.object_id=t.object_id AND c.name='Label') AND EXISTS (SELECT 1 FROM tempdb.sys.columns c WHERE c.object_id=t.object_id AND c.name='Id');";
                using(var r=cmd.ExecuteReader())while(r.Read())if(!baseline.Contains(r.GetInt32(0))) {
                    candidates.Add(r.GetInt32(0));Log("Storage candidate object="+r.GetInt32(0)+" name="+r.GetString(1));
                }
            }
            var observed=new HashSet<string>();
            foreach(int oid in candidates) {
                var pages=new List<Tuple<int,int>>();
                using(var cmd=connection.CreateCommand()) {
                    cmd.CommandTimeout=5;cmd.CommandText="SELECT allocated_page_file_id,allocated_page_page_id FROM sys.dm_db_database_page_allocations(DB_ID('tempdb'),@oid,NULL,NULL,'DETAILED') WHERE is_allocated=1 AND page_type=1;";
                    cmd.Parameters.AddWithValue("@oid",oid);
                    using(var r=cmd.ExecuteReader())while(r.Read())pages.Add(Tuple.Create(Convert.ToInt32(r[0]),Convert.ToInt32(r[1])));
                }
                foreach(var page in pages)using(var cmd=connection.CreateCommand()) {
                    cmd.CommandTimeout=5;
                    cmd.CommandText=string.Format(System.Globalization.CultureInfo.InvariantCulture,"DBCC PAGE (tempdb, {0}, {1}, 3) WITH TABLERESULTS, NO_INFOMSGS;",page.Item1,page.Item2);
                    using(var r=cmd.ExecuteReader())do {
                        while(r.Read()) {
                            string parent=Convert.ToString(r[0]), obj=Convert.ToString(r[1]),field=Convert.ToString(r[2]),value=Convert.ToString(r[3]);
                            if(field=="Id"||field=="Label") {
                                Log("STORAGE ROW object="+oid+" page="+page.Item1+":"+page.Item2+" "+parent+" "+obj+" "+field+"="+value);
                                if(field=="Label")observed.Add(value);
                            }
                        }
                    }while(r.NextResult());
                }
            }
            storageVerified=observed.SetEquals(new[]{"table variable alpha","table variable beta","temp alpha","temp beta"});
            Log("PAUSED STORAGE VERIFICATION="+storageVerified);
        }catch(Exception ex){Log("Storage inspection failed: "+ex.Message);}
    }
    static void Log(string s) {Console.WriteLine(DateTime.UtcNow.ToString("o")+" "+s);}
    static string Hr(int h) {return "0x"+h.ToString("X8");}
    [MTAThread] public static int Main(string[] args) {
        bool started=false;
        try {
            if(IntPtr.Size!=8)throw new Exception("Probe requires x64.");
            Log("CoInitializeSecurity="+Hr(CoInitializeSecurity(IntPtr.Zero,-1,IntPtr.Zero,IntPtr.Zero,2,3,IntPtr.Zero,0,IntPtr.Zero)));
            SqlConnectionStringBuilder cb=new SqlConnectionStringBuilder();cb.DataSource=args.Length>0?args[0]:@"(localdb)\mssqllocaldb";
            cb.IntegratedSecurity=true;cb.InitialCatalog="tempdb";cb.Pooling=false;cb.ConnectTimeout=10;cb.ApplicationName="Codex SQL Native Debugger Feasibility";
            using(SqlConnection control=new SqlConnection(cb.ConnectionString)) {
                control.Open();Log("Connected SQL "+control.ServerVersion);byte[] blob;
                var baseline=new HashSet<int>();
                using(var cmd=control.CreateCommand()) {
                    cmd.CommandText="SELECT object_id FROM tempdb.sys.tables";
                    using(var r=cmd.ExecuteReader())while(r.Read())baseline.Add(r.GetInt32(0));
                }
                inspector=delegate {InspectStorage(control,baseline);};SetInspector(inspector);
                using(SqlCommand cmd=control.CreateCommand()) {
                    cmd.CommandTimeout=10;cmd.CommandText="DECLARE @b varbinary(8000); EXEC master.dbo.sp_enable_sql_debug @b OUTPUT; SELECT @b;";
                    blob=(byte[])cmd.ExecuteScalar();
                }
                Log("Interface bytes="+blob.Length);IStream stream;
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero,true,out stream));
                try {
                    stream.Write(blob,blob.Length,IntPtr.Zero);stream.Seek(0,0,IntPtr.Zero);
                    Guid iid=new Guid("87BC18DC-C8B3-11D5-AE96-00B0D0E93CC1");IntPtr ptr;
                    int hr=CoUnmarshalInterface(stream,ref iid,out ptr);Log("CoUnmarshalInterface="+Hr(hr));Marshal.ThrowExceptionForHR(hr);
                    try {Marshal.ThrowExceptionForHR(StartProbe(ptr,Environment.MachineName,(uint)System.Diagnostics.Process.GetCurrentProcess().Id));started=true;}
                    finally{Marshal.Release(ptr);}
                } finally{Marshal.ReleaseComObject(stream);}
                using(SqlConnection worker=new SqlConnection(cb.ConnectionString)) {
                    worker.Open();using(SqlCommand cmd=worker.CreateCommand()) {
                        cmd.CommandTimeout=20;cmd.CommandText=@"CREATE PROCEDURE #CodexDebugProbe AS
BEGIN
DECLARE @probeScalar int = 0;
DECLARE @probeTable TABLE (Id int, Label nvarchar(40));
CREATE TABLE #ProbeTemp (Id int, Label nvarchar(40));
INSERT @probeTable VALUES (11, N'table variable alpha'), (22, N'table variable beta');
INSERT #ProbeTemp VALUES (33, N'temp alpha'), (44, N'temp beta');
SET @probeScalar = 314159;
SET @probeScalar = @probeScalar + 1;
SELECT @probeScalar AS CompletedValue,
       (SELECT COUNT(*) FROM @probeTable) AS TableVariableRows,
       (SELECT COUNT(*) FROM #ProbeTemp) AS TempTableRows;
END";
                        cmd.ExecuteNonQuery();cmd.CommandText="EXEC #CodexDebugProbe";
                        using(SqlDataReader reader=cmd.ExecuteReader()) {
                            if(!reader.Read())throw new Exception("Fixture returned no control result.");
                            int scalar=reader.GetInt32(0),tv=reader.GetInt32(1),tt=reader.GetInt32(2);
                            Log("Fixture control result scalar="+scalar+" tableVariableRows="+tv+" tempTableRows="+tt);
                            if(scalar!=314160||tv!=2||tt!=2)throw new Exception("Fixture control result mismatch.");
                            while(reader.NextResult())while(reader.Read()){}
                        }
                    }
                    int steps=FinishProbe();started=false;GC.KeepAlive(inspector);return steps>0&&storageVerified?0:2;
                }
            }
        }catch(Exception ex){Log("FAILED "+ex);return 1;}
        finally{if(started)FinishProbe();}
    }
}
