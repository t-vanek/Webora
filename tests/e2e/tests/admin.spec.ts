import { test, expect } from '@playwright/test';
import { ADMIN_STATE } from './helpers';

test.use({ storageState: ADMIN_STATE });

test.describe('Administration', () => {
  test('users list shows the header chip, panel and create action', async ({ page }) => {
    await page.goto('/admin/users');
    await expect(page.locator('.count-chip')).toBeVisible();
    await expect(page.locator('.admin-panel')).toBeVisible();
    await expect(page.getByRole('link', { name: /Nový účet/ })).toBeVisible();
    await expect(page.locator('.admin-panel').getByText('admin@webora.local')).toBeVisible();
  });

  test('roles list renders the seeded roles', async ({ page }) => {
    await page.goto('/admin/roles');
    await expect(page.locator('.count-chip')).toBeVisible();
    await expect(page.locator('.admin-panel')).toContainText('Administrator');
  });

  test('parking spots list shows colour-coded type pills', async ({ page }) => {
    await page.goto('/admin/parking/spots');
    await expect(page.locator('.type-pill').first()).toBeVisible();
    await expect(page.locator('.state-pill').first()).toBeVisible();
  });

  test('rules & pricing tabs switch content', async ({ page }) => {
    await page.goto('/admin/parking/settings');
    await expect(page.getByText('Ekonomika rezervací')).toBeVisible();
    await page.locator('fluent-tab', { hasText: 'Důvěra a ochrana' }).click();
    await expect(page.getByText('Graf důvěry', { exact: true })).toBeVisible();
  });
});
