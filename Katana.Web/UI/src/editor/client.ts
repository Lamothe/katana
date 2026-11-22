import { abortable } from './abortable.ts';
import type { DeviceState, ParameterDefinition, PresetSummary } from './types';

export interface KatanaClientOptions {
  getUrl: (path: string) => string;
  authorisation?: () => Promise<string>;
  credentials?: RequestCredentials;
}

export function createKatanaApi(options: KatanaClientOptions) {
  async function request<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
    signal?.throwIfAborted();
    const accessToken = await abortable(Promise.resolve(options.authorisation?.()), signal);
    signal?.throwIfAborted();
    const response = await fetch(options.getUrl(path), {
      ...(options.credentials === undefined ? {} : { credentials: options.credentials }),
      method,
      signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(10_000)]) : AbortSignal.timeout(10_000),
      headers: {
        Accept: 'application/json',
        ...(method === 'GET' ? {} : { 'Content-Type': 'application/json' }),
        ...(accessToken === undefined ? {} : { Authorization: `Bearer ${accessToken}` }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (!response.ok) {
      let message = `Request failed (${response.status}).`;
      try {
        const problem: unknown = await response.json();
        if (typeof problem === 'object' && problem !== null) {
          if ('detail' in problem && typeof problem.detail === 'string' && problem.detail) {
            message = problem.detail;
          } else if ('title' in problem && typeof problem.title === 'string' && problem.title) {
            message = problem.title;
          } else if ('error' in problem && typeof problem.error === 'string' && problem.error) {
            message = problem.error;
          }
        }
      } catch { /* Preserve the HTTP error for non-JSON responses. */ }
      throw new Error(message);
    }
    if (response.status === 204) return undefined as T;
    return await response.json() as T;
  }

  const presetPath = (id: string) => `/presets/${encodeURIComponent(id)}`;

  return {
    parameters: () => request<ParameterDefinition[]>('/parameters'),
    connect: (signal?: AbortSignal) => request<DeviceState>('/device/connect', 'POST', undefined, signal),
    monitor: () => request<DeviceState | null>('/device/monitor'),
    connection: () => request<{ connected: boolean }>('/device/connection'),
    device: () => request<DeviceState>('/device'),
    setParameter: (id: string, value: number, signal?: AbortSignal) => request<DeviceState>(`/device/parameters/${encodeURIComponent(id)}`, 'PUT', { value }, signal),
    setListeningLevel: (decibels: number, configureOutputEq = false) => request<DeviceState>('/device/listening-level', 'PUT', { decibels, configureOutputEq }),
    setUsbAudioLevel: (output: 'processed' | 'dry', percent: number) => request<DeviceState>(`/device/usb-audio/${output}`, 'PUT', { percent }),
    recallSlot: (slot: number) => request<DeviceState>(`/device/slots/${slot}/recall`, 'POST'),
    commit: (slot = 1) => request<DeviceState>(`/device/slots/${slot}/commit`, 'POST'),
    presets: () => request<PresetSummary[]>('/presets'),
    savePreset: (name: string) => request<PresetSummary>('/presets', 'POST', { name }),
    renamePreset: (id: string, name: string) => request<PresetSummary>(presetPath(id), 'PUT', { name }),
    deletePreset: (id: string) => request<void>(presetPath(id), 'DELETE'),
    applyPreset: (id: string, slot = 1) => request<DeviceState>(`${presetPath(id)}/slots/${slot}/apply`, 'POST'),
  };
}
