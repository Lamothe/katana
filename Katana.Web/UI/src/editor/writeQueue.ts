// One in-flight request; pending changes to the same control replace older values.
// Consumers merge every response with pendingValues so late replies cannot undo edits.
export class ParameterWriteQueue<T> {
  private readonly pending = new Map<string, number>();
  private disposed = false;
  private readonly abort = new AbortController();
  private current: { id: string; value: number } | null = null;

  private readonly write: (id: string, value: number, signal?: AbortSignal) => Promise<T>;
  private readonly onResponse: (response: T) => void;
  private readonly onError: (error: unknown) => void;
  private readonly onChange: () => void;

  constructor(
    write: (id: string, value: number, signal?: AbortSignal) => Promise<T>,
    onResponse: (response: T) => void,
    onError: (error: unknown) => void,
    onChange: () => void,
  ) {
    this.write = write;
    this.onResponse = onResponse;
    this.onError = onError;
    this.onChange = onChange;
  }

  get busy(): boolean {
    return this.current !== null || this.pending.size > 0;
  }

  get pendingValues(): Record<string, number> {
    return {
      ...(this.current ? { [this.current.id]: this.current.value } : {}),
      ...Object.fromEntries(this.pending),
    };
  }

  enqueue(id: string, value: number): void {
    if (this.disposed) return;
    this.pending.set(id, value);
    this.onChange();
    if (this.current === null) void this.drain();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.pending.clear();
    this.current = null;
    this.abort.abort();
  }

  private async drain(): Promise<void> {
    while (!this.disposed && this.pending.size > 0) {
      const next = this.pending.entries().next().value;
      if (!next) break;
      const [id, value] = next;
      this.pending.delete(id);
      this.current = { id, value };
      this.onChange();
      try {
        const response = await this.write(id, value, this.abort.signal);
        if (this.disposed) return;
        this.current = null;
        this.onResponse(response);
      } catch (error) {
        if (this.disposed) return;
        // Stop after an uncertain write: do not continue changing the amp blindly.
        this.current = null;
        this.pending.clear();
        this.onError(error);
      }
      this.onChange();
    }
  }
}
