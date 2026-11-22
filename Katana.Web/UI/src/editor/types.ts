export interface ParameterOption {
  value: number;
  label: string;
}

export interface ParameterDefinition {
  id: string;
  label: string;
  group: string;
  min: number;
  max: number;
  unit: string;
  options: ParameterOption[] | null;
  displayOffset?: number;
  displayScale?: number;
  dependsOn?: string | null;
  dependsOnValue?: number | null;
}

export interface DeviceState {
  connected: boolean;
  identity: string | null;
  values: Record<string, number>;
  patchName: string | null;
  slot: number | null;
  usbAudioLevels?: { available: boolean; processedPercent: number | null; dryPercent: number | null } | null;
  listeningLevel?: { available: boolean; enabled: boolean; outputPosition: boolean; decibels: number | null } | null;
}

export interface PresetSummary {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
}
