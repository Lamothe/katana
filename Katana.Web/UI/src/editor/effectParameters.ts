import type { ParameterDefinition } from './types';

export function parameterVisible(parameter: ParameterDefinition, values: Record<string, number>): boolean {
  return !parameter.dependsOn || values[parameter.dependsOn] === parameter.dependsOnValue;
}

export function parameterDisplay(parameter: ParameterDefinition, raw: number): string {
  const value = (raw + (parameter.displayOffset ?? 0)) * (parameter.displayScale ?? 1);
  return new Intl.NumberFormat('en-AU', { maximumFractionDigits: 3 }).format(value);
}

export function parameterWriteDisabled(
  parameter: ParameterDefinition, parameters: ParameterDefinition[], pending: Record<string, number>, writing: boolean,
): boolean {
  // Finish earlier edits before changing algorithm; wait for verified selector replies
  // before editing its newly visible controls. Never initialise algorithm defaults.
  return (writing && parameters.some(item => item.dependsOn === parameter.id))
    || (!!parameter.dependsOn && pending[parameter.dependsOn] !== undefined);
}

export function isOnOffParameter(parameter: ParameterDefinition): boolean {
  return parameter.min === 0 && parameter.max === 1 && parameter.options?.length === 2
    && parameter.options.some(option => option.value === 0 && option.label === 'Off')
    && parameter.options.some(option => option.value === 1 && option.label === 'On');
}

export function effectEnabled(group: string, parameters: ParameterDefinition[], values: Record<string, number>): boolean {
  const enable = parameters.find(parameter => parameter.group === group && parameter.label === 'Enabled'
    && isOnOffParameter(parameter));
  return !!enable && values[enable.id] === 1;
}

export function effectTabLabel(group: string, parameters: ParameterDefinition[], values: Record<string, number>): string {
  const enable = parameters.find(parameter => parameter.group === group && parameter.label === 'Enabled'
    && isOnOffParameter(parameter));
  if (!enable) return group;
  const value = values[enable.id];
  return `${group}: ${value === 1 ? 'On' : value === 0 ? 'Off' : 'Not available'}`;
}
