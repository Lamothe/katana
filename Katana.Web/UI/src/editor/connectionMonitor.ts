// Do not let a snapshot that started before a user action overwrite newer state.
export class ConnectionMonitor<T extends { connected: boolean } = { connected: boolean }> {
  private revision = 0;
  private checking = false;

  private readonly probe: () => Promise<T | null>;
  private readonly blocked: () => boolean;
  private readonly onOffline: (failure?: unknown) => void;
  private readonly onOnline: ((state: T) => void) | undefined;

  constructor(
    probe: () => Promise<T | null>,
    blocked: () => boolean,
    onOffline: (failure?: unknown) => void,
    onOnline?: (state: T) => void,
  ) {
    this.probe = probe;
    this.blocked = blocked;
    this.onOffline = onOffline;
    this.onOnline = onOnline;
  }

  invalidate(): void { this.revision++; }

  async check(): Promise<void> {
    if (this.checking || this.blocked()) return;
    this.checking = true;
    const revision = this.revision;
    try {
      const result = await this.probe();
      if (result && revision === this.revision && !this.blocked()) {
        if (result.connected) this.onOnline?.(result);
        else this.onOffline();
      }
    } catch (failure) {
      if (revision === this.revision && !this.blocked()) this.onOffline(failure);
    } finally {
      this.checking = false;
    }
  }
}
