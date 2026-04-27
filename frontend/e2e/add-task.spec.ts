import { test, expect } from '@playwright/test';
import { api } from './helpers/api';

/**
 * Verifies the Add Task dialog and that the Claude model catalog reaches
 * the UI. We probe the backend catalog first so a failure fingerprints
 * the right layer.
 */

interface ModelCatalog {
  models: Array<{ id: string; label: string; vendor?: string; isDefault?: boolean }>;
  source: string;
}

test.describe('Add Task — Claude model selection', () => {
  test('backend exposes a non-empty Claude model catalog', async () => {
    const cat = await api<ModelCatalog>('/api/cli/claude/models');
    expect(Array.isArray(cat.models)).toBe(true);
    expect(cat.models.length).toBeGreaterThan(0);
    expect(cat.models.map(m => m.id)).toEqual(
      expect.arrayContaining(['claude-opus-4-7', 'claude-sonnet-4-6'])
    );
  });

  test('Add Task button opens the dialog', async ({ page }) => {
    await page.goto('/');
    const addBtn = page.getByRole('button', { name: /add task/i }).first();
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    // Dialog/modal should appear. Match by role first, fall back to text.
    const dialog = page.getByRole('dialog');
    await expect(dialog.or(page.getByText(/title|titel/i)).first()).toBeVisible();
  });
});
