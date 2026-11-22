import { app, BrowserWindow, dialog, protocol, session } from 'electron';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const directory = fileURLToPath(new URL('.', import.meta.url));
const backendPath = join(directory, 'dist', 'backend');
const scheme = 'katana';
const origin = 'katana://editor';

protocol.registerSchemesAsPrivileged([{
  scheme,
  privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
}]);

let backend;
let mainWindow;
let quitting = false;

function startBackend(token) {
  backend = spawn('dotnet', [join(backendPath, 'Katana.Web.dll')], {
    cwd: backendPath,
    env: { ...process.env, KATANA_DESKTOP_TOKEN: token },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  backend.stdin.on('error', () => {});
  backend.stderr.pipe(process.stderr);
  const lines = createInterface({ input: backend.stdout });

  return new Promise((resolve, reject) => {
    let started = false;
    const timeout = setTimeout(() => reject(new Error('The Katana backend did not report a listening address.')), 15_000);
    backend.on('error', error => {
      clearTimeout(timeout);
      reject(error);
    });
    backend.on('exit', (code, signal) => {
      clearTimeout(timeout);
      const error = new Error(`The Katana backend exited (${signal ?? code}).`);
      if (!started) reject(error);
      else if (!quitting) {
        dialog.showErrorBox('Katana backend disconnected', error.message);
        app.quit();
      }
    });
    lines.on('line', line => {
      const match = /^KATANA_DESKTOP_READY (http:\/\/127\.0\.0\.1:\d+)$/.exec(line);
      if (!match) {
        console.log(line);
        return;
      }
      started = true;
      clearTimeout(timeout);
      resolve(match[1]);
    });
  });
}

async function launch() {
  const token = randomBytes(32).toString('hex');
  const address = await startBackend(token);
  protocol.handle(scheme, async request => {
    if (!request.url.startsWith(origin + '/') ||
        (request.initiatorOrigin && request.initiatorOrigin !== origin)) {
      return new Response(null, { status: 403 });
    }

    const url = new URL(request.url);
    const headers = new Headers({ 'X-Katana-Desktop-Token': token });
    for (const name of ['accept', 'content-type']) {
      const value = request.headers.get(name);
      if (value !== null) headers.set(name, value);
    }
    try {
      return await fetch(address + url.pathname + url.search, {
        method: request.method,
        body: request.body,
        headers,
        duplex: 'half',
        redirect: 'error',
        signal: request.signal,
      });
    } catch (error) {
      if (!request.signal.aborted) console.error('Katana backend request failed', error);
      return new Response(null, { status: 503 });
    }
  });

  session.defaultSession.setPermissionCheckHandler(() => false);
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));

  mainWindow = new BrowserWindow({
    title: 'Katana',
    icon: join(directory, 'katana.png'),
    width: 1240,
    height: 900,
    minWidth: 700,
    minHeight: 560,
    backgroundColor: '#141414',
    webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false },
  });
  mainWindow.removeMenu();
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  mainWindow.webContents.on('will-frame-navigate', event => event.preventDefault());
  await mainWindow.loadURL(origin + '/');
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (!mainWindow || mainWindow.isDestroyed()) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });
  app.on('window-all-closed', () => app.quit());
  app.on('before-quit', event => {
    if (!backend?.pid || backend.exitCode !== null || backend.signalCode !== null) return;
    event.preventDefault();
    if (quitting) return;
    quitting = true;
    const exited = once(backend, 'exit');
    backend.stdin.end();
    void exited.then(() => app.quit());
  });
  app.whenReady().then(launch).catch(error => {
    dialog.showErrorBox('Katana could not start', error.message);
    app.quit();
  });
}
