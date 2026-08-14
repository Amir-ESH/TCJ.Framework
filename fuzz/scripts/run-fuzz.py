#!/usr/bin/env python3
from __future__ import annotations
import argparse, json, subprocess, sys, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def load_policy(): return json.loads((ROOT/'eng/fuzzing-policy.json').read_text(encoding='utf-8'))
def load_targets(): return json.loads((ROOT/'fuzz/targets.json').read_text(encoding='utf-8'))['targets']

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--duration', type=int, required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--target', action='append')
    parser.add_argument('--seed', type=int, default=39039)
    parser.add_argument('--configuration', default='Release')
    args=parser.parse_args()
    policy=load_policy(); wanted=set(args.target or policy['requiredFuzzTargets'])
    targets=[t for t in load_targets() if t['name'] in wanted]
    missing=wanted-{t['name'] for t in targets}
    if missing: raise SystemExit(f"Unknown fuzz targets: {sorted(missing)}")
    output=(ROOT/args.output).resolve() if not Path(args.output).is_absolute() else Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    failures=0
    for index,target in enumerate(targets):
        target_out=output/target['name']; target_out.mkdir(parents=True,exist_ok=True)
        cmd=['dotnet','run','--no-build','--project',str(ROOT/policy['fuzzProject']),'-c',args.configuration,'--','--managed',
             '--target',target['name'],'--duration',str(args.duration),'--corpus',str(ROOT/target['corpus']),'--output',str(target_out),
             '--seed',str(args.seed+index),'--max-input-bytes',str(policy['maximumInputBytes']),
             '--timeout-ms',str(policy['maximumSingleExecutionMilliseconds']),'--max-memory-bytes',str(policy['maximumProcessMemoryBytes'])]
        started=time.monotonic(); classification='Pass'; returncode=0
        try:
            proc=subprocess.run(cmd,cwd=ROOT,text=True,capture_output=True,timeout=args.duration+15,check=False)
            returncode=proc.returncode
            (target_out/'stdout.log').write_text(proc.stdout[-20000:],encoding='utf-8')
            (target_out/'stderr.log').write_text(proc.stderr[-20000:],encoding='utf-8')
            if returncode != 0: classification='Crash'
        except subprocess.TimeoutExpired as exc:
            classification='Hang'; returncode=124
            (target_out/'stderr.log').write_text('Managed fuzz process exceeded the external watchdog timeout.\n',encoding='utf-8')
        elapsed=time.monotonic()-started
        runner={'target':target['name'],'status':'Pass' if returncode==0 else 'Fail','classification':classification,
                'returnCode':returncode,'wallClockSeconds':round(elapsed,3),'requestedDurationSeconds':args.duration}
        (target_out/'runner-result.json').write_text(json.dumps(runner,indent=2),encoding='utf-8')
        if returncode != 0: failures+=1
    return 1 if failures else 0
if __name__=='__main__': raise SystemExit(main())
