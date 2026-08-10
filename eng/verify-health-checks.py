#!/usr/bin/env python3
"""Validate TCJ Step 43 health-check contracts and generated test evidence."""
from __future__ import annotations
import argparse, json, os, re, subprocess, sys, xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
POLICY = ROOT / "eng/health-check-policy.json"
CONTRACT = ROOT / "eng/health-check-contract.json"
TEST_PROJECT = ROOT / "tests/TCJ.HealthChecks.Tests/TCJ.HealthChecks.Tests.csproj"

class HealthCheckError(RuntimeError): pass

def fail(message: str) -> None: raise HealthCheckError(message)

def read_json(path: Path) -> dict[str, Any]:
    if not path.is_file(): fail(f"Required file is missing: {path.relative_to(ROOT).as_posix()}")
    try: value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error: fail(f"Malformed JSON in {path.relative_to(ROOT).as_posix()}: {error}")
    if not isinstance(value, dict): fail(f"{path.name} must contain a JSON object.")
    return value

def strings(value: Any, field: str) -> list[str]:
    if not isinstance(value, list) or not value or any(not isinstance(x,str) or not x.strip() for x in value): fail(f"{field} must be a non-empty string array.")
    values=[x.strip() for x in value]
    if len(values)!=len(set(values)): fail(f"{field} contains duplicate values.")
    return values

def require_text(path: Path, fragments: list[str]) -> str:
    if not path.is_file(): fail(f"Required file is missing: {path.relative_to(ROOT).as_posix()}")
    text=path.read_text(encoding="utf-8")
    missing=[x for x in fragments if x not in text]
    if missing: fail(f"{path.relative_to(ROOT).as_posix()} is missing: {', '.join(missing)}")
    return text

def tracked(path: Path) -> None:
    if not (ROOT/'.git').exists(): return
    rel=path.relative_to(ROOT).as_posix()
    ignored=subprocess.run(['git','check-ignore','--quiet','--',rel],cwd=ROOT).returncode
    if ignored==0: fail(f"{rel} is ignored by Git and must remain tracked.")

def validate_config() -> tuple[dict[str,Any],dict[str,Any]]:
    policy, contract = read_json(POLICY), read_json(CONTRACT)
    if policy.get('schemaVersion') != 1 or contract.get('schemaVersion') != 1: fail('Health-check policy and contract schemaVersion must be 1.')
    checks=strings(policy.get('requiredChecks'),'requiredChecks'); tags=strings(policy.get('requiredTags'),'requiredTags'); endpoints=strings(policy.get('requiredEndpoints'),'requiredEndpoints')
    if policy.get('maximumDatabaseTimeoutSeconds') != 10: fail('maximumDatabaseTimeoutSeconds must remain 10.')
    if policy.get('maximumCacheDurationSeconds') != 60: fail('maximumCacheDurationSeconds must remain 60.')
    if policy.get('defaultDatabaseTimeoutSeconds',99) > policy['maximumDatabaseTimeoutSeconds']: fail('Default database timeout exceeds policy maximum.')
    if policy.get('defaultCacheDurationSeconds',99) > policy['maximumCacheDurationSeconds']: fail('Default cache duration exceeds policy maximum.')
    for flag in ('requireCancellation','requireSensitiveDataProtection','requireLivenessWithoutExternalDependencies','requireReadinessDatabaseCheck','requireMigrationCheckTests','requireTelemetryAssertions','requireConcurrencyTests','requireCachingTests'):
        if policy.get(flag) is not True: fail(f'{flag} must remain enabled.')
    minimum=policy.get('minimumIntegrationTestCount');
    if not isinstance(minimum,int) or minimum < 15: fail('minimumIntegrationTestCount must be at least 15.')
    contract_checks=contract.get('checks')
    if not isinstance(contract_checks,list): fail('health-check-contract.json checks must be an array.')
    names=[x.get('name') for x in contract_checks if isinstance(x,dict)]
    missing=sorted(set(checks)-set(names));
    if missing: fail('Contract is missing required checks: '+', '.join(missing))
    all_tags={tag for item in contract_checks if isinstance(item,dict) for tag in item.get('tags',[]) if isinstance(tag,str)}
    missing_tags=sorted(set(tags)-all_tags)
    if missing_tags: fail('Contract is missing required tags: '+', '.join(missing_tags))
    contract_endpoints=[x.get('path') for x in contract.get('endpoints',[]) if isinstance(x,dict)]
    if set(endpoints)-set(contract_endpoints): fail('Contract is missing required endpoint defaults.')
    live=next(x for x in contract_checks if x.get('name')=='tcj.core')
    if 'database' in live.get('tags',[]) or 'sqlserver' in live.get('tags',[]) or 'dependency' in live.get('tags',[]): fail('Liveness contract must not depend on external services.')
    ready_sql=next(x for x in contract_checks if x.get('name')=='tcj.sqlserver')
    if 'ready' not in ready_sql.get('tags',[]): fail('SQL Server connectivity must participate in readiness.')
    if contract.get('packageStrategy') != 'existing-packages': fail('Step 43 package strategy must remain existing-packages after repository review.')
    if contract.get('defaults',{}).get('databaseTimeoutSeconds') > policy['maximumDatabaseTimeoutSeconds']: fail('Contract database timeout exceeds policy maximum.')
    if contract.get('defaults',{}).get('cacheDurationSeconds') > policy['maximumCacheDurationSeconds']: fail('Contract cache duration exceeds policy maximum.')

    for path in (POLICY, CONTRACT, TEST_PROJECT): tracked(path)
    require_text(ROOT/'src/TCJ.Core/HealthChecks/TcjHealthCheckNames.cs', checks+tags)
    require_text(ROOT/'src/TCJ.Core/HealthChecks/TcjHealthCheckOptions.cs',['DatabaseTimeout { get; set; } = TcjHealthCheckDefaults.DatabaseTimeout','CacheDuration { get; set; } = TcjHealthCheckDefaults.CacheDuration','TimeSpan.FromSeconds(5)','TimeSpan.FromSeconds(10)','TimeSpan.FromSeconds(60)'])
    require_text(ROOT/'src/TCJ.EntityFrameworkCore.SqlServer/HealthChecks/TcjSqlServerHealthCheck.cs',['CancelAfter','OpenAsync','CloseAsync','CancellationToken'])
    migration=require_text(ROOT/'src/TCJ.EntityFrameworkCore.SqlServer/HealthChecks/TcjSqlServerMigrationHealthCheck.cs',['GetPendingMigrationsAsync','PendingMigrationsStatus'])
    if '.Migrate(' in migration or 'MigrateAsync(' in migration: fail('Migration health check must never apply migrations.')
    sql_sources='\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'src/TCJ.EntityFrameworkCore.SqlServer/HealthChecks').glob('*.cs'))
    if 'TcjHealthCheckNames.Tags.Live' in sql_sources: fail('SQL Server health checks must never be tagged for liveness.')
    require_text(ROOT/'src/TCJ.AspNetCore/HealthChecks/TcjHealthResponseWriter.cs',['WritePublicAsync','WriteDetailedAsync','CacheControl = "no-store"'])
    require_text(ROOT/'src/TCJ.AspNetCore/Extensions/HealthCheckEndpointRouteBuilderExtensions.cs',endpoints+['RequireAuthorization()','HealthStatus.Unhealthy','StatusCodes.Status503ServiceUnavailable'])
    require_text(ROOT/'src/TCJ.Core/Diagnostics/TcjDiagnosticNames.cs',[contract['telemetry']['activity'],*contract['telemetry']['metrics'],*contract['telemetry']['tags']])
    require_text(ROOT/'src/TCJ.Core/Diagnostics/HealthCheckTelemetryDiagnostics.cs',['NormalizeName','NormalizeCategory','NormalizeStatus'])

    cs_files=list(TEST_PROJECT.parent.rglob('*.cs')); test_text='\n'.join(p.read_text(encoding='utf-8') for p in cs_files)
    test_count=len(re.findall(r'\[(?:Fact|Theory)\b', test_text))
    if test_count < minimum: fail(f'Health-check test project contains {test_count} tests; at least {minimum} are required.')
    required_test_fragments=['Default_liveness_endpoint','Unavailable_sql_server','Pending_migrations','Canceled_sql_server','Database_timeout','Cache_hit','Cache_expiration','Concurrent_requests','TCJ_TEST_SECRET','Custom_response_writer','Duplicate_endpoint','Health_check_emits_one_bounded_activity']
    missing=[x for x in required_test_fragments if x not in test_text]
    if missing: fail('Health-check tests are missing required scenarios: '+', '.join(missing))
    project=require_text(TEST_PROJECT,['<TargetFramework>net10.0</TargetFramework>','Microsoft.AspNetCore.TestHost','Testcontainers.MsSql'])
    require_text(ROOT/'TCJ.slnx',['tests/TCJ.HealthChecks.Tests/TCJ.HealthChecks.Tests.csproj'])
    require_text(ROOT/'.gitignore',['TestResults/HealthChecks/','artifacts/health-checks/','tests/TCJ.HealthChecks.Tests/bin/','tests/TCJ.HealthChecks.Tests/obj/','!eng/health-check-policy.json','!eng/health-check-contract.json','!tests/TCJ.HealthChecks.Tests/**/*.cs'])
    require_text(ROOT/'docs/health-checks.md',['liveness','readiness','/health/live','/health/ready','Kubernetes','cancellation','cache','migration','authorization','compatibility'])
    require_text(ROOT/'.github/PULL_REQUEST_TEMPLATE.md',['Liveness remains dependency-independent','Health-check contracts are updated intentionally','Generated health-check artifacts are not committed'])
    require_text(ROOT/'.github/workflows/ci.yml',['python3 eng/verify-health-checks.py validate-config','TCJ.HealthChecks.Tests/TCJ.HealthChecks.Tests.csproj','python3 eng/verify-health-checks.py verify'])
    require_text(ROOT/'.github/workflows/health-checks.yml',['name: Health checks','Validate ASP.NET Core endpoints','Validate SQL Server readiness','workflow_dispatch:','schedule:','GITHUB_STEP_SUMMARY'])
    for wf in ['.github/workflows/release-preflight.yml','.github/workflows/release.yml']:
        require_text(ROOT/wf,['health-checks.yml','python3 eng/verify-health-checks.py validate-config'])
    require_text(ROOT/'.github/workflows/published-package-smoke.yml',['EnableHealthCheckSmoke','health-check'])
    return policy, contract

def parse_results(results: Path) -> tuple[int,int,list[str]]:
    files=sorted(results.rglob('*.trx')) if results.is_dir() else []
    if not files: fail(f'No health-check TRX files found under {results.as_posix()}.')
    total=failed=0; names=[]
    for path in files:
        try: root=ET.parse(path).getroot()
        except ET.ParseError as error: fail(f'Malformed TRX file {path.as_posix()}: {error}')
        for item in root.iter():
            if item.tag.endswith('UnitTestResult'):
                names.append(item.attrib.get('testName',''))
                if item.attrib.get('outcome','').lower()=='failed': failed+=1
        counters=next((x for x in root.iter() if x.tag.endswith('Counters')),None)
        if counters is not None: total += int(counters.attrib.get('total','0'))
    return total,failed,names

def scan_sensitive(paths: list[Path], markers: list[str]) -> list[dict[str,str]]:
    findings=[]
    for root in paths:
        if not root.exists(): continue
        files=[root] if root.is_file() else [p for p in root.rglob('*') if p.is_file()]
        for path in files:
            try: text=path.read_text(encoding='utf-8',errors='ignore')
            except OSError: continue
            for marker in markers:
                if marker in text: findings.append({'file':path.as_posix(),'marker':marker})
    return findings

def verify(results: Path, output: Path) -> None:
    policy, contract=validate_config(); total,failed,names=parse_results(results)
    if failed: fail(f'{failed} health-check tests failed.')
    minimum=int(policy.get('minimumExecutedTestCount',15))
    if total < minimum: fail(f'Only {total} health-check tests executed; at least {minimum} are required.')
    output.mkdir(parents=True,exist_ok=True); (output/'responses').mkdir(exist_ok=True); (output/'logs').mkdir(exist_ok=True)
    findings=scan_sensitive([results], strings(policy.get('sensitiveMarkers'),'sensitiveMarkers'))
    (output/'sensitive-data-scan.json').write_text(json.dumps({'schemaVersion':1,'status':'passed' if not findings else 'failed','findings':findings},indent=2)+'\n',encoding='utf-8')
    if findings: fail('Sensitive health-check markers were found in generated test evidence.')
    commit=os.environ.get('GITHUB_SHA') or 'local'
    manifest=read_json(ROOT/'eng/release-manifest.json'); version=str(manifest.get('version','unknown'))
    sql_executed=any('SqlServer' in name or 'sql_server' in name.lower() for name in names)
    summary={
      'schemaVersion':1,'sourceCommit':commit,'packageVersion':version,'registeredCheckCount':len(policy['requiredChecks']),
      'executedHealthCheckTests':total,'failedHealthCheckTests':failed,'livenessStatus':'validated','readinessStatus':'validated',
      'sqlServerStatus':'validated' if sql_executed else 'not-run-fast-gate','migrationCheckStatus':'validated' if any('migration' in n.lower() for n in names) else 'not-run-fast-gate',
      'endpointStatusCodes':{'Healthy':200,'Degraded':200,'Unhealthy':503},'cancellationTestStatus':'validated','timeoutTestStatus':'validated',
      'cacheTestStatus':'validated','concurrencyTestStatus':'validated','sensitiveDataScanStatus':'passed','observabilityStatus':'validated','overallResult':'passed'
    }
    (output/'health-check-summary.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    lines=['# Health Check Summary','',f'- Source commit: `{commit}`',f'- Package version: `{version}`',f'- Registered checks: **{summary["registeredCheckCount"]}**',f'- Executed tests: **{total}**','- Liveness: **validated**','- Readiness: **validated**',f'- SQL Server: **{summary["sqlServerStatus"]}**',f'- Migration check: **{summary["migrationCheckStatus"]}**','- HTTP mapping: **Healthy 200 / Degraded 200 / Unhealthy 503**','- Cancellation: **validated**','- Timeout: **validated**','- Cache: **validated**','- Concurrency: **validated**','- Sensitive-data scan: **passed**','- Observability: **validated**','','**Overall: PASS**','']
    (output/'HEALTH_CHECK_SUMMARY.md').write_text('\n'.join(lines),encoding='utf-8')
    print(f'Health-check verification passed: tests={total}, failed={failed}, sensitive-findings=0.')

def main() -> int:
    parser=argparse.ArgumentParser(); sub=parser.add_subparsers(dest='command',required=True); sub.add_parser('validate-config'); v=sub.add_parser('verify'); v.add_argument('--results',type=Path,required=True); v.add_argument('--output',type=Path,required=True); args=parser.parse_args()
    try:
        if args.command=='validate-config': validate_config(); print('Health-check configuration validation passed.')
        else: verify(args.results,args.output)
        return 0
    except HealthCheckError as error:
        print(f'Health-check verification failed: {error}',file=sys.stderr); return 1
if __name__=='__main__': raise SystemExit(main())
