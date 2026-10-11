# Native SQL Server debugger feasibility result

**Follow-up:** [STORAGE-FEASIBILITY.md](STORAGE-FEASIBILITY.md) demonstrates a successful alternative using a separate connection to read data pages while paused. This document records the earlier symbol-interface-only result.

Test date: October 8, 2026 (America/Chicago). The log uses UTC, so its timestamps are October 9.

## Conclusion

**No-go for building the requested table inspector solely on the tested native symbol interfaces.** A custom debugger can use this interface family to step statements and inspect scalar locals. On this build, going beneath SSDT does not reveal table-variable rows: the server returns a leaf containing `(table)`, with no child enumerator. Temporary tables are absent from the local-symbol enumeration, and the named global-symbol API rejects their names.

This is a negative result for these interfaces on this instance, not proof that every undocumented approach or every server version is impossible.

## Tested environment

- Instance: `(localdb)\mssqllocaldb`, Windows authentication, sysadmin membership confirmed.
- SQL Server: 2019 Express LocalDB, 15.0.4382.1, RTM-CU27-GDR (KB5040948), x64.
- Installed Visual Studio: Community 2022, 17.14.36717.8.
- Installed SSDT native SQL debug engine file version: 17.0.0.1.
- SQL COM proxy found at `C:\Program Files\Common Files\Microsoft Shared\SQL Debugging\130\ssdebugps.dll`.
- The final probe calls the server's COM interface directly; it does not call SSDT's Watch expression evaluator or load its debug engine.

## Evidence

| Check | Observed result |
|---|---|
| `sp_enable_sql_debug` | Returned a 340-byte marshaled interface |
| `CoUnmarshalInterface` for `IHostDebugServerInstance` | `S_OK` |
| Register process and receive connection callbacks | `S_OK`, callback received |
| Request initial break using `IHostDebug::AsyncBreak` | `S_OK` |
| Step into disposable procedure using `Continue` | Nine step callbacks across setup, invocation, and procedure statements |
| `IHostDebug::GetSyms` at scalar marker | `@probeScalar = 314159`, confirming access to the paused scope |
| Table-variable symbol at the same pause | name `@probeTable`, type `table`, flags `2`, kind `0` (leaf), value `(table)` |
| Compound children for table variable | No compound symbol / child enumerator was returned |
| Temporary table in local symbols | Not returned; the two locals were the scalar and table variable |
| `GetGlobalSym("@@SPID")` | `S_OK`, value `65` in the captured run |
| `GetGlobalSym("#ProbeTemp")` | `0x80070057` (`E_INVALIDARG`) |
| `GetGlobalSym("@probeTable")` | `0x80070057` (`E_INVALIDARG`) |
| Fixture's ordinary final result | Scalar `314160`; table variable 2 rows; temporary table 2 rows |
| Stop debugging and unregister | Both `S_OK` |
| Independent cleanup query after completion | Zero probe sessions and zero temporary probe procedures remaining |

The global lookup failures are consistent with its role as a global-variable lookup, not a general query interface. They are not independently sufficient to rule out all possible temporary-table access. The stronger table-variable evidence is the actual server-returned leaf representation despite confirmed population and correct scalar inspection.

Relevant excerpt from the successful run:

```text
Symbol depth=0 name=@probeScalar type=int flags=2 kind=0 value=314159
Symbol depth=0 name=@probeTable type=table flags=2 kind=0 value=(table)
GetGlobalSym name=@@SPID hr=00000000
Symbol depth=0 name=@@SPID type=smallint flags=33 kind=0 value=65
GetGlobalSym name=#ProbeTemp hr=80070057
GetGlobalSym name=@probeTable hr=80070057
...
Fixture control result scalar=314160 tableVariableRows=2 tempTableRows=2
Native StopDebugging=00000000
Native Unregister=00000000
RESULT steps=9 markerSeen=1 tableSymbols=7 tableChildren=0
```

## Scope and implications

The fixture was executed intact after creation: no checkpoints, serialized snapshots, or debug statements were inserted. The final ordinary SELECT is part of the fixture and verifies its data independently of the debugger. No existing user procedure was altered. The temporary procedure and its data are connection-scoped.

Initial managed COM callback code terminated in the CLR with an access violation. Replacing those callbacks with native C++ avoided that failure. An explicit initial asynchronous break was also necessary before step commands could run. The captured final log is the complete successful native-callback run; the earlier crash is not treated as evidence about SQL Server's table capabilities.

The remaining possible research directions would involve different private interfaces, server-memory/storage analysis, or a separate strategy for temporary tables. None of those was validated here. A new UI alone would not fix the table-variable result returned by `GetSyms`.

## Sources

- [Microsoft SQL Server 2022 security guidance addendum](https://download.microsoft.com/download/a/6/b/a6b82edc-c0ab-4c29-a4dd-f5979143dd41/SQL22_EAL4-W_AGD_ADD_1.2.pdf): documents the `sp_enable_sql_debug` entry point and marshaled `IHostDebugServerInstance` return path. Used to identify the entry point, not to assert SQL Server 2019 table support.
- [Microsoft's SQL debugger patent, US7500225B2, appendix](https://patents.google.com/patent/US7500225B2/en): historical definitions of the native callback and symbol interfaces. This is not a current compatibility contract.
- [Microsoft debugger permission documentation](https://learn.microsoft.com/en-us/visualstudio/debugger/error-user-could-not-execute-stored-procedure-sp-enable-sql-debug?view=visualstudio): requirements for calling the debugger entry point.

The feasibility conclusion is based on the local runtime evidence in `probe-result.log`, not on assuming that historical interface definitions guarantee table support.
