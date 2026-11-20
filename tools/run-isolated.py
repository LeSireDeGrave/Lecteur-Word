#!/usr/bin/env python3
"""Linux resource supervisor, not a filesystem/network sandbox. One CLI child only."""
import argparse, os, pathlib, resource, signal, subprocess, sys, time
p=argparse.ArgumentParser()
p.add_argument('--rss-mib',type=int,default=768)
p.add_argument('--seconds',type=float,default=45)
p.add_argument('command',nargs=argparse.REMAINDER)
a=p.parse_args()
if a.rss_mib<32 or a.seconds<=0 or not a.command:p.error('Invalid budgets/command')
def limits():
    resource.setrlimit(resource.RLIMIT_CPU,(31,32))
    resource.setrlimit(resource.RLIMIT_FSIZE,(128*1024*1024,128*1024*1024))
    resource.setrlimit(resource.RLIMIT_CORE,(0,0))
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_GCHeapHardLimit='0x18000000')
child=subprocess.Popen(a.command,env=env,preexec_fn=limits,start_new_session=True)
started=time.monotonic()
try:
    while child.poll() is None:
        try:
            rss=next(int(l.split()[1])*1024 for l in pathlib.Path(f'/proc/{child.pid}/status').read_text().splitlines() if l.startswith('VmRSS:'))
        except (FileNotFoundError,StopIteration):rss=0
        if rss>a.rss_mib*1024*1024 or time.monotonic()-started>a.seconds:
            os.killpg(child.pid,signal.SIGKILL);child.wait()
            print('SUPERVISOR_RESOURCE_LIMIT',file=sys.stderr);sys.exit(75)
        time.sleep(.01)
    sys.exit(child.returncode if child.returncode>=0 else 128-child.returncode)
finally:
    if child.poll() is None:os.killpg(child.pid,signal.SIGKILL);child.wait()
