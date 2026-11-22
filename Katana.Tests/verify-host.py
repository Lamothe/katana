"""Hardware-free standalone startup/listener check. Build Katana.Web first; needs Python 3 + ss."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
from urllib.request import urlopen

host = Path(__file__).resolve().parents[1] / "Katana.Web"
assembly = host / "bin/Debug/net10.0/Katana.Web.dll"
# Reserve three available ports until immediately before starting the test host.
reservations = [socket.socket() for _ in range(3)]
for reservation in reservations:
    reservation.bind(("127.0.0.1", 0))
port, endpoint_port, hosting_port = [reservation.getsockname()[1] for reservation in reservations]
with tempfile.TemporaryDirectory(prefix="katana-host-test-") as temporary:
    environment = os.environ.copy()
    environment.update({
        "Katana__PresetDirectory": temporary,
        "Katana__MidiPort": "unused-test-port",
        "Katana__Port": str(port),
        "Kestrel__Endpoints__Injected__Url": f"http://0.0.0.0:{endpoint_port}",
        "ASPNETCORE_PREFERHOSTINGURLS": "true",
        "ASPNETCORE_URLS": f"http://0.0.0.0:{hosting_port}",
    })
    with (Path(temporary) / "startup.log").open("w") as log:
        for reservation in reservations:
            reservation.close()
        process = subprocess.Popen(["dotnet", str(assembly)], cwd=host, env=environment, stdout=log, stderr=subprocess.STDOUT)
        try:
            for attempt in range(100):
                if process.poll() is not None:
                    raise RuntimeError("Standalone exited during startup")
                try:
                    with urlopen(f"http://127.0.0.1:{port}/api/device", timeout=1) as response:
                        assert json.load(response)["connected"] is False
                    break
                except OSError:
                    time.sleep(0.1)
            else:
                raise TimeoutError("Standalone did not become ready")
            listeners = subprocess.check_output(["ss", "-ltn", f"( sport = :{port} or sport = :{endpoint_port} or sport = :{hosting_port} )"], text=True)
            assert f"127.0.0.1:{port}" in listeners, listeners
            assert f":{endpoint_port}" not in listeners and f":{hosting_port}" not in listeners, listeners
            with urlopen(f"http://127.0.0.1:{port}/api/parameters", timeout=3) as response:
                assert len(json.load(response)) > 0
            with urlopen(f"http://127.0.0.1:{port}/", timeout=3) as response:
                assert response.status == 200
            print("PASS: sole loopback listener despite Kestrel/hosting URL injection; UI/API ready, amp disconnected")
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
