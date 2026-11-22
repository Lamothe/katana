<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import type { createKatanaApi } from './client';
import ParameterControl from './ParameterControl.vue';
import { effectTabLabel, effectEnabled, isOnOffParameter, parameterVisible, parameterWriteDisabled } from './effectParameters';
import type { DeviceState, ParameterDefinition, PresetSummary } from './types';
import { ParameterWriteQueue } from './writeQueue';
import { ConnectionMonitor } from './connectionMonitor';

const props = defineProps<{ api: ReturnType<typeof createKatanaApi> }>();
const api = props.api;

const parameters = ref<ParameterDefinition[]>([]);
const device = ref<DeviceState>({ connected: false, identity: null, values: {}, patchName: null, slot: null });
const presets = ref<PresetSummary[]>([]);
const initialising = ref(true);
const slotDialog = ref<HTMLDialogElement | null>(null);
const selectedSlot = ref(1);
const slotOperation = ref<'recall' | 'commit' | 'apply'>('recall');
const slotPreset = ref<PresetSummary | null>(null);
const confirmationSlot = ref(1);
const slotLabel = (slot: number): string => slot === 0 ? 'Panel' : `${slot <= 4 ? 'A' : 'B'}${(slot - 1) % 4 + 1}`;
const slotChoices = Array.from({ length: 8 }, (_, index) => index + 1);
const usbAudioDialog = ref<HTMLDialogElement | null>(null);
const usbAudioOpen = ref(false);
const action = ref('');
const error = ref('');
const notice = ref('');
const needsRefresh = ref(false);
const writing = ref(false);
const pendingValues = ref<Record<string, number>>({});
const activeGroup = ref('');
const presetName = ref('');
const editingId = ref<string | null>(null);
const renameName = ref('');

const queue = new ParameterWriteQueue(
  api.setParameter,
  (response) => { device.value = response; },
  (failure) => {
    monitor.invalidate();
    error.value = `${message(failure)} Writes stopped. Refresh the amp before editing or saving again.`;
    needsRefresh.value = true;
  },
  () => {
    writing.value = queue.busy;
    pendingValues.value = queue.pendingValues;
  },
);

const busy = computed(() => initialising.value || writing.value || !!action.value);
const canEdit = computed(() => device.value.connected && !initialising.value && !action.value && !needsRefresh.value);
const canCapture = computed(() => canEdit.value && !busy.value);
const values = computed(() => ({ ...device.value.values, ...pendingValues.value }));
const groups = computed(() => [...new Set(parameters.value.map(parameter => parameter.group))]);
const amplifierGroup = computed(() => groups.value.find(group => /amp|preamp/i.test(group)) ?? groups.value[0]);
const amplifierParameters = computed(() => parameters.value.filter(parameter => parameter.group === amplifierGroup.value));
const effectGroups = computed(() => groups.value.filter(group => group !== amplifierGroup.value));
const effectParameters = computed(() => parameters.value.filter(parameter => parameter.group === activeGroup.value && parameterVisible(parameter, values.value)));
const effectEnableParameter = computed(() => effectParameters.value.find(parameter => parameter.label === 'Enabled' && isOnOffParameter(parameter)));
const effectControlParameters = computed(() => effectParameters.value.filter(parameter => parameter !== effectEnableParameter.value));
function parameterRows(items: ParameterDefinition[]): ParameterDefinition[][] {
  return [items.filter(parameter => !!parameter.options?.length), items.filter(parameter => !parameter.options?.length)]
    .filter(row => row.length > 0);
}
const effectDetailsUnavailable = computed(() => {
  const selector = parameters.value.find(parameter => parameter.group === activeGroup.value
    && (parameter.id === 'modType' || parameter.id === 'fxType'));
  return !!selector && values.value[selector.id] !== undefined
    && !effectParameters.value.some(parameter => parameter.dependsOn === selector.id);
});
const sortedPresets = computed(() => [...presets.value].sort((a, b) => a.name.localeCompare(b.name, 'en-AU')));

let disposed = false;
const pointerActive = ref(false);
const keyboardActive = ref(false);
const interacting = computed(() => pointerActive.value || keyboardActive.value);
function beginInteraction() { pointerActive.value = true; monitor.invalidate(); }
function endInteraction() { pointerActive.value = false; }
function beginKeyboardInteraction() { keyboardActive.value = true; monitor.invalidate(); }
function endKeyboardInteraction() { keyboardActive.value = false; }
function cancelInteraction() { endInteraction(); endKeyboardInteraction(); }

const monitor = new ConnectionMonitor(
  api.monitor,
  () => disposed || document.hidden || interacting.value || busy.value || needsRefresh.value || !device.value.connected,
  (failure) => {
    // A native modal would otherwise hide the global connection error toast.
    closeSlotDialog();
    device.value = { connected: false, identity: null, values: {}, patchName: null, slot: null };
    notice.value = '';
    error.value = failure
      ? `Connection check failed: ${message(failure)} Reconnect when the editor and amp are available.`
      : '';
  },
  state => {
    const slotChanged = state.slot !== null && state.slot !== device.value.slot;
    device.value = state;
    // Preserve an intentionally selected save destination on ordinary knob updates.
    if (slotChanged) syncSelectedSlot();
  },
);
const connectionAbort = new AbortController();
let heartbeat: ReturnType<typeof setInterval> | undefined;

function message(failure: unknown): string {
  return failure instanceof Error ? failure.message : 'An unexpected error occurred.';
}

async function perform(label: string, operation: () => Promise<void>): Promise<void> {
  if (busy.value) return;
  monitor.invalidate();
  action.value = label;
  error.value = '';
  notice.value = '';
  try { await operation(); }
  catch (failure) { error.value = message(failure); }
  finally { action.value = ''; }
}

async function load(): Promise<void> {
  error.value = '';
  try {
    const [definitions, current, library] = await Promise.all([api.parameters(), api.device(), api.presets()]);
    parameters.value = definitions;
    device.value = current;
    syncSelectedSlot();
    presets.value = library;
    activeGroup.value = effectGroups.value[0] ?? '';
  } catch (failure) { error.value = message(failure); }
  finally { initialising.value = false; }
}

function connect(): void {
  if (device.value.connected) return;
  void perform('Connecting to amp…', async () => {
    device.value = { ...device.value, connected: false };
    const current = await api.connect(connectionAbort.signal);
    connectionAbort.signal.throwIfAborted();
    device.value = current;
    syncSelectedSlot();
    needsRefresh.value = false;
    // Also recover metadata/library if the initial load failed.
    parameters.value = await api.parameters();
    presets.value = await api.presets();
    if (!effectGroups.value.includes(activeGroup.value)) activeGroup.value = effectGroups.value[0] ?? '';
    notice.value = device.value.connected ? '' : 'No amp connected.';
  });
}

function openUsbAudio(): void {
  if (!device.value.connected || busy.value) return;
  usbAudioDialog.value?.showModal();
  usbAudioOpen.value = !!usbAudioDialog.value?.open;
}

function closeUsbAudio(): void {
  usbAudioDialog.value?.close();
  usbAudioOpen.value = false;
}

function setUsbAudioLevel(output: 'processed' | 'dry', event: Event): void {
  if (!canCapture.value || !device.value.usbAudioLevels?.available) return;
  const percent = Number((event.target as HTMLInputElement).value);
  void perform('Updating USB recording level…', async () => {
    try { device.value = await api.setUsbAudioLevel(output, percent); }
    catch (failure) { needsRefresh.value = true; throw failure; }
  });
}

function refresh(): void {
  if (!device.value.connected) return;
  void perform('Refreshing amp and library…', async () => {
    const [current, definitions, library] = await Promise.all([api.device(), api.parameters(), api.presets()]);
    device.value = current;
    syncSelectedSlot();
    parameters.value = definitions;
    presets.value = library;
    needsRefresh.value = false;
    if (!effectGroups.value.includes(activeGroup.value)) activeGroup.value = effectGroups.value[0] ?? '';
  });
}

function dismissFeedback(): void {
  error.value = '';
  notice.value = '';
}

function changeParameter(id: string, value: number): void {
  const parameter = parameters.value.find(item => item.id === id);
  if (!canEdit.value || !parameter || !parameterVisible(parameter, values.value)
    || parameterWriteDisabled(parameter, parameters.value, pendingValues.value, writing.value)) return;
  error.value = '';
  monitor.invalidate();
  queue.enqueue(id, value);
}

function savePreset(): void {
  const name = presetName.value.trim();
  if (!canCapture.value || !name) return;
  void perform('Capturing app preset…', async () => {
    const preset = await api.savePreset(name);
    presets.value.push(preset);
    presetName.value = '';
    notice.value = `“${preset.name}” saved in the app library. The amp slot was not committed.`;
  });
}

function startRename(preset: PresetSummary): void {
  if (busy.value) return;
  editingId.value = preset.id;
  renameName.value = preset.name;
}

function renamePreset(): void {
  const id = editingId.value;
  const name = renameName.value.trim();
  if (!id || !name) return;
  void perform('Renaming preset…', async () => {
    const updated = await api.renamePreset(id, name);
    presets.value = presets.value.map(preset => preset.id === id ? updated : preset);
    editingId.value = null;
    notice.value = 'Preset renamed.';
  });
}

function deletePreset(preset: PresetSummary): void {
  if (busy.value || !window.confirm(`Delete “${preset.name}” from the app library? This cannot be undone.`)) return;
  void perform('Deleting preset…', async () => {
    await api.deletePreset(preset.id);
    presets.value = presets.value.filter(item => item.id !== preset.id);
    if (editingId.value === preset.id) editingId.value = null;
    notice.value = 'Preset deleted from the app library.';
  });
}

function syncSelectedSlot(): void {
  const slot = device.value.slot;
  if (device.value.connected && slot !== null && Number.isInteger(slot) && slot >= 0 && slot <= 8) {
    selectedSlot.value = slot;
  }
}

function requestSlotOperation(operation: 'recall' | 'commit' | 'apply', preset: PresetSummary | null = null): void {
  if (!canCapture.value) return;
  const slot = selectedSlot.value;
  if (!Number.isInteger(slot) || slot < (operation === 'recall' ? 0 : 1) || slot > 8) return;
  slotOperation.value = operation;
  slotPreset.value = preset;
  confirmationSlot.value = slot;
  slotDialog.value?.showModal();
}

function closeSlotDialog(): void {
  slotDialog.value?.close();
}

function confirmSlotOperation(): void {
  if (!slotDialog.value?.open) return;
  closeSlotDialog();
  if (!canCapture.value) return;
  const operation = slotOperation.value;
  const preset = slotPreset.value;
  const slot = confirmationSlot.value;
  if (operation === 'apply' && !preset) return;
  void perform(operation === 'recall' ? 'Loading amp slot…' : 'Saving amp slot…', async () => {
    try {
      device.value = operation === 'recall' ? await api.recallSlot(slot)
        : operation === 'apply' && preset ? await api.applyPreset(preset.id, slot) : await api.commit(slot);
      syncSelectedSlot();
      if (!effectGroups.value.includes(activeGroup.value)) activeGroup.value = effectGroups.value[0] ?? '';
    } catch (failure) {
      needsRefresh.value = true;
      throw new Error(`${message(failure)} Reconnect before editing again; the amp may have changed.`, { cause: failure });
    }
  });
}

function applyPreset(preset: PresetSummary): void {
  requestSlotOperation('apply', preset);
}

function commit(): void {
  requestSlotOperation('commit');
}

function date(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? 'Unknown date' : parsed.toLocaleDateString('en-AU', {
    day: '2-digit', month: '2-digit', year: 'numeric',
  });
}

onMounted(() => {
  window.addEventListener('pointerup', endInteraction);
  window.addEventListener('pointercancel', endInteraction);
  window.addEventListener('keyup', endKeyboardInteraction);
  window.addEventListener('blur', cancelInteraction);
  void load();
  heartbeat = setInterval(() => { void monitor.check(); }, 2000);
});
onUnmounted(() => {
  disposed = true;
  queue.dispose();
  window.removeEventListener('pointerup', endInteraction);
  window.removeEventListener('pointercancel', endInteraction);
  window.removeEventListener('keyup', endKeyboardInteraction);
  window.removeEventListener('blur', cancelInteraction);
  closeUsbAudio();
  closeSlotDialog();
  connectionAbort.abort();
  clearInterval(heartbeat);
  monitor.invalidate();
});
</script>

<template>
  <div class="katana-editor"
       @pointerdown.capture="beginInteraction"
       @pointerup.capture="endInteraction"
       @pointercancel.capture="endInteraction"
       @keydown.capture="beginKeyboardInteraction"
       @keyup.capture="endKeyboardInteraction"
  >
    <dialog ref="slotDialog"
            class="slot-dialog"
            aria-labelledby="slot-dialog-heading"
            aria-describedby="slot-dialog-description"
            @cancel.prevent="closeSlotDialog"
    >
      <h3 id="slot-dialog-heading">{{ slotOperation === 'recall' ? `Load ${slotLabel(confirmationSlot)}?` : `Overwrite ${slotLabel(confirmationSlot)}?` }}</h3>
      <p id="slot-dialog-description"
         class="muted"
      >
        {{ slotOperation === 'recall'
          ? 'Unsaved live changes will be discarded. Stored sounds will not be overwritten.'
          : `This replaces the stored sound in ${slotLabel(confirmationSlot)} with ${slotOperation === 'apply' ? `“${slotPreset?.name}”` : 'your working sound'} and selects that slot.` }}
      </p>
      <div class="slot-dialog-actions">
        <button type="button"
                autofocus
                @click="closeSlotDialog"
        >
          Cancel
        </button>
        <button type="button"
                class="primary"
                :disabled="!canCapture"
                @click="confirmSlotOperation"
        >
          {{ slotOperation === 'recall' ? 'Load slot' : 'Overwrite slot' }}
        </button>
      </div>
    </dialog>
    <dialog ref="usbAudioDialog"
            class="usb-audio-dialog"
            aria-labelledby="usb-audio-heading"
            aria-describedby="usb-audio-description"
            @cancel.prevent="closeUsbAudio"
    >
      <div class="card-heading">
        <h3 id="usb-audio-heading">USB Audio</h3>
        <button type="button"
                class="quiet"
                autofocus
                @click="closeUsbAudio"
        >
          Close
        </button>
      </div>
      <p id="usb-audio-description"
         class="muted"
      >
        Amp-wide recording levels, separate from your patches. 100% is the normal level.
      </p>
      <div class="usb-levels controls-grid">
        <div v-for="output in (['processed', 'dry'] as const)"
             :key="output"
             class="parameter fader-parameter usb-level"
        >
          <div class="parameter-heading">
            <label :for="`usb-${output}-level`">{{ output === 'processed' ? 'Processed recording' : 'Dry recording' }}</label>
          </div>
          <div class="vertical-range">
            <input :id="`usb-${output}-level`"
                   type="range"
                   min="0"
                   max="200"
                   step="2"
                   :value="device.usbAudioLevels?.[output === 'processed' ? 'processedPercent' : 'dryPercent'] ?? 0"
                   :disabled="!canCapture || !device.usbAudioLevels?.available"
                   :aria-label="output === 'processed' ? 'Processed USB recording level' : 'Dry USB recording level'"
                   aria-orientation="vertical"
                   @change="setUsbAudioLevel(output, $event)"
            />
            <div class="range-labels"
                 aria-hidden="true"
            >
              <span>200%</span><span>0%</span>
            </div>
          </div>
          <output :for="`usb-${output}-level`"
                  :class="{ 'usb-muted': device.usbAudioLevels?.[output === 'processed' ? 'processedPercent' : 'dryPercent'] === 0 }"
          >{{ device.usbAudioLevels?.available ? `${device.usbAudioLevels[output === 'processed' ? 'processedPercent' : 'dryPercent']}%` : 'Unavailable' }}<span v-if="device.usbAudioLevels?.available && device.usbAudioLevels[output === 'processed' ? 'processedPercent' : 'dryPercent'] === 0"> · Muted</span></output>
        </div>
      </div>
      <p v-if="!device.connected"
         class="muted"
      >
        Amp offline
      </p>
      <div v-if="error || notice"
           class="katana-toast katana-dialog-toast"
           :class="{ 'katana-toast-error': !!error }"
      >
        <p :role="error ? 'alert' : 'status'">{{ error || notice }}</p>
        <button type="button"
                aria-label="Dismiss notification"
                @click="dismissFeedback"
        >
          ×
        </button>
      </div>
    </dialog>
    <Teleport to="body">
      <div v-if="!usbAudioOpen && (error || notice)"
           class="katana-toast"
           :class="{ 'katana-toast-error': !!error }"
      >
        <p :role="error ? 'alert' : 'status'">{{ error || notice }}</p>
        <button type="button"
                aria-label="Dismiss notification"
                @click="dismissFeedback"
        >
          ×
        </button>
      </div>
    </Teleport>
    <div class="app-shell">
      <header class="topbar">
        <div class="brand">
          <span class="brand-mark"
                aria-hidden="true"
          ><span class="brand-glyph">刀</span></span><div><h1>Katana</h1><p>ARTIST · LOCAL EDITOR</p></div>
        </div>
        <div class="connection-actions">
          <span class="connection"
                :class="{ connected: device.connected }"
          ><span aria-hidden="true" />{{ device.connected ? 'Amp online' : 'Amp offline' }}</span>
          <button v-if="device.connected"
                  class="quiet"
                  :disabled="busy"
                  @click="refresh"
          >
            Refresh
          </button>
        </div>
      </header>

      <main>
        <div v-if="device.connected"
             class="workspace"
        >
          <section class="card"
                   aria-labelledby="amp-heading"
          >
            <div class="card-heading">
              <div><p class="eyebrow">01 / AMPLIFIER</p><h3 id="amp-heading">Tone &amp; character</h3></div><div class="amp-heading-actions">
                <button type="button"
                        class="quiet"
                        aria-haspopup="dialog"
                        :disabled="!device.connected || busy"
                        @click="openUsbAudio"
                >
                  USB Audio
                </button><span class="tag">LIVE</span>
              </div>
            </div>
            <div v-if="amplifierParameters.length"
                 class="control-rows"
            >
              <div v-for="(row, index) in parameterRows(amplifierParameters)"
                   :key="index"
                   class="controls-grid"
                   :class="{ 'option-controls': row.some(parameter => parameter.options?.length) }"
              >
                <ParameterControl v-for="parameter in row"
                                  :key="parameter.id"
                                  :parameter="parameter"
                                  :value="values[parameter.id]"
                                  :disabled="!canEdit || parameterWriteDisabled(parameter, parameters, pendingValues, writing)"
                                  @change="changeParameter"
                />
              </div>
            </div>
            <p v-else
               class="empty"
            >
              No parameter metadata loaded. Connect or refresh to try again.
            </p>
          </section>

          <section v-if="effectGroups.length"
                   class="card"
                   aria-labelledby="effects-heading"
          >
            <div class="card-heading"><div><p class="eyebrow">02 / EFFECTS</p><h3 id="effects-heading">Build your texture</h3></div></div>
            <nav class="effect-tabs"
                 aria-label="Effect groups"
            >
              <button v-for="group in effectGroups"
                      :key="group"
                      :class="{ selected: activeGroup === group, enabled: effectEnabled(group, parameters, values) }"
                      :aria-pressed="activeGroup === group"
                      :aria-label="effectTabLabel(group, parameters, values)"
                      @click="activeGroup = group"
              >
                {{ group }}
              </button>
            </nav>
            <div class="effect-panel">
              <div class="effect-panel-heading">
                <h4>{{ activeGroup }}</h4>
                <ParameterControl v-if="effectEnableParameter"
                                  :parameter="effectEnableParameter"
                                  :value="values[effectEnableParameter.id]"
                                  :disabled="!canEdit"
                                  inline
                                  @change="changeParameter"
                />
              </div>
              <div class="control-rows">
                <div v-for="(row, index) in parameterRows(effectControlParameters)"
                     :key="index"
                     class="controls-grid"
                     :class="{ 'option-controls': row.some(parameter => parameter.options?.length) }"
                >
                  <ParameterControl v-for="parameter in row"
                                    :key="parameter.id"
                                    :parameter="parameter"
                                    :value="values[parameter.id]"
                                    :disabled="!canEdit || parameterWriteDisabled(parameter, parameters, pendingValues, writing)"
                                    @change="changeParameter"
                  />
                </div>
              </div>
              <p v-if="effectDetailsUnavailable"
                 class="muted compact"
              >
                Detailed controls for this effect are not available yet. Its existing settings are preserved.
              </p>
            </div>
          </section>

          <aside class="card library"
                 aria-labelledby="library-heading"
          >
            <div class="card-heading"><div><p class="eyebrow">03 / PATCH MANAGER</p><h3 id="library-heading">Your sounds</h3></div></div>
            <div class="amp-slots">
              <div class="slot-controls">
                <div class="slot-field">
                  <label for="amp-slot">Slot</label>
                  <select id="amp-slot"
                          v-model.number="selectedSlot"
                          :disabled="!canCapture"
                  >
                    <option :value="0">Panel</option>
                    <option v-for="slot in slotChoices"
                            :key="slot"
                            :value="slot"
                    >
                      {{ slotLabel(slot) }}
                    </option>
                  </select>
                </div>
                <button type="button"
                        :disabled="!canCapture"
                        @click="requestSlotOperation('recall')"
                >
                  Load
                </button>
                <button type="button"
                        class="primary"
                        :disabled="!canCapture || selectedSlot === 0"
                        @click="commit"
                >
                  Save
                </button>
              </div>
            </div>
            <div class="patch-section-heading"><h4>Your presets</h4><span class="count">{{ presets.length }}</span></div>
            <form class="save-form"
                  @submit.prevent="savePreset"
            >
              <input id="preset-name"
                     v-model="presetName"
                     aria-label="Preset name"
                     type="text"
                     placeholder="e.g. Warm clean"
                     maxlength="80"
                     :disabled="busy"
                     autocomplete="off"
              />
              <button class="primary"
                      :disabled="!canCapture || !presetName.trim()"
                      type="submit"
              >
                Save app preset
              </button>
            </form>
            <p v-if="!presets.length"
               class="empty"
            >
              No app presets yet. Dial in a sound, give it a name and save it here.
            </p>
            <ul v-else
                class="preset-list"
            >
              <li v-for="preset in sortedPresets"
                  :key="preset.id"
              >
                <template v-if="editingId !== preset.id">
                  <div class="preset-title"><strong>{{ preset.name }}</strong><small>Updated {{ date(preset.updatedAt) }}</small></div>
                  <div class="preset-actions">
                    <button :disabled="!canCapture || selectedSlot === 0"
                            :aria-label="`Apply ${preset.name} and save to ${slotLabel(selectedSlot)}`"
                            @click="applyPreset(preset)"
                    >
                      Apply → {{ slotLabel(selectedSlot) }}
                    </button>
                    <button class="quiet"
                            :disabled="busy"
                            :aria-label="`Rename ${preset.name}`"
                            @click="startRename(preset)"
                    >
                      Rename
                    </button>
                    <button class="quiet danger"
                            :disabled="busy"
                            :aria-label="`Delete ${preset.name}`"
                            @click="deletePreset(preset)"
                    >
                      Delete
                    </button>
                  </div>
                </template>
                <form v-else
                      class="rename-form"
                      @submit.prevent="renamePreset"
                >
                  <label :for="`rename-${preset.id}`">Rename {{ preset.name }}</label>
                  <input :id="`rename-${preset.id}`"
                         v-model="renameName"
                         type="text"
                         maxlength="80"
                         :disabled="busy"
                  />
                  <div class="preset-actions">
                    <button type="submit"
                            :disabled="busy || !renameName.trim()"
                    >
                      Save name
                    </button><button class="quiet"
                                     type="button"
                                     :disabled="busy"
                                     @click="editingId = null"
                    >
                      Cancel
                    </button>
                  </div>
                </form>
              </li>
            </ul>
          </aside>
        </div>
        <section v-else
                 class="offline-card"
                 aria-label="Amp offline"
        >
          <button type="button"
                  class="primary"
                  :disabled="busy"
                  @click="connect"
          >
            {{ action.includes('amp') ? action : 'Connect amp' }}
          </button>
        </section>
        <footer v-if="device.identity">Device: {{ device.identity }}</footer>
      </main>
    </div>
  </div>
</template>

<style src="./glyph.css"></style>
<style src="./style.css"></style>
