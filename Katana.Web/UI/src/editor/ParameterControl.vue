<script setup lang="ts">
import type { ParameterDefinition } from './types';
import { isOnOffParameter, parameterDisplay } from './effectParameters';

const props = defineProps<{
  parameter: ParameterDefinition;
  value: number | undefined;
  disabled: boolean;
  inline?: boolean;
}>();
const emit = defineEmits<{ change: [id: string, value: number] }>();

function change(event: Event): void {
  const target = event.target;
  if (!(target instanceof HTMLInputElement || target instanceof window.HTMLSelectElement)) return;
  const value = Number(target.value);
  if (!Number.isFinite(value) || value < props.parameter.min || value > props.parameter.max) return;
  if (props.disabled || props.value === undefined) return;
  if (props.parameter.options?.length && !props.parameter.options.some(option => option.value === value)) return;
  emit('change', props.parameter.id, value);
}

function toggle(): void {
  if (props.disabled
    || (props.value !== 0 && props.value !== 1) || !isOnOffParameter(props.parameter)) return;
  emit('change', props.parameter.id, props.value === 1 ? 0 : 1);
}

function label(): string {
  return props.parameter.id.toLowerCase().replace(/[^a-z]/g, '') === 'volume'
    || props.parameter.label.toLowerCase() === 'volume'
    ? 'Channel volume' : props.parameter.label;
}
</script>

<template>
  <div class="parameter"
       :class="{ unavailable: value === undefined, 'fader-parameter': !parameter.options?.length, 'option-parameter': !!parameter.options?.length }"
  >
    <div v-if="!inline"
         class="parameter-heading"
    >
      <label :for="`parameter-${parameter.id}`">{{ label() }}</label>
    </div>
    <button v-if="isOnOffParameter(parameter)"
            :id="`parameter-${parameter.id}`"
            type="button"
            class="effect-toggle"
            :class="{ active: value === 1 }"
            :aria-label="`${parameter.group}: ${label()}`"
            :aria-pressed="value === 1"
            :disabled="disabled || (value !== 0 && value !== 1)"
            @click="toggle"
    >
      {{ label() }}
    </button>
    <select v-else-if="parameter.options?.length"
            :id="`parameter-${parameter.id}`"
            :value="value ?? ''"
            :disabled="disabled || value === undefined"
            @change="change"
    >
      <option v-if="value === undefined"
              disabled
              value=""
      >
        Not available
      </option>
      <option v-else-if="!parameter.options.some(option => option.value === value)"
              :value="value"
      >
        Unknown ({{ value }})
      </option>
      <option v-for="option in parameter.options"
              :key="option.value"
              :value="option.value"
      >
        {{ option.label }}
      </option>
    </select>
    <div v-else
         class="vertical-range"
    >
      <input :id="`parameter-${parameter.id}`"
             type="range"
             aria-orientation="vertical"
             :min="parameter.min"
             :max="parameter.max"
             step="1"
             :value="value ?? parameter.min"
             :disabled="disabled || value === undefined"
             :aria-valuetext="value === undefined ? 'Not available' : `${parameterDisplay(parameter, value)} ${parameter.unit}`"
             @input="change"
      />
      <div class="range-labels"
           aria-hidden="true"
      >
        <span>{{ parameterDisplay(parameter, parameter.max) }}</span><span>{{ parameterDisplay(parameter, parameter.min) }}</span>
      </div>
    </div>
    <output v-if="!parameter.options?.length"
            :for="`parameter-${parameter.id}`"
    >
      {{ value === undefined ? '—' : parameterDisplay(parameter, value) }}<span v-if="value !== undefined && parameter.unit"> {{ parameter.unit }}</span>
    </output>
  </div>
</template>
