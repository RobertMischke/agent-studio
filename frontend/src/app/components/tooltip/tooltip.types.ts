export type TooltipPosition = 'top' | 'bottom' | 'left' | 'right' | 'auto';

export type TooltipSeverity = 'info' | 'warn' | 'error' | 'success';

export interface StructuredTooltip {
  title?: string;
  body: string;
}

export type TooltipInput = string | StructuredTooltip | null | undefined;
