import { Page, expect } from '@playwright/test';

/** Seeded administrator from IdentitySeed in appsettings.json. */
export const ADMIN = { email: 'admin@webora.local', password: 'Admin123$' };

/** Stored admin session produced by auth.setup.ts and reused by authenticated specs. */
export const ADMIN_STATE = '.auth/admin.json';

/**
 * Fluent text fields are form-associated custom elements; the real <input> lives
 * in the shadow DOM, so reach through to it.
 */
export async function fillFluentField(page: Page, name: string, value: string): Promise<void> {
  const input = page.locator(`fluent-text-field[name="${name}"]`).locator('input');
  await input.waitFor({ state: 'visible' });
  await input.fill(value);
}

export async function submitForm(page: Page): Promise<void> {
  await page.locator('fluent-button[type="submit"], button[type="submit"]').first().click();
}

export async function login(
  page: Page,
  email: string = ADMIN.email,
  password: string = ADMIN.password,
): Promise<void> {
  await page.goto('/login');
  await fillFluentField(page, 'Input.Email', email);
  await fillFluentField(page, 'Input.Password', password);
  await submitForm(page);
}

/** Confirms the signed-in chrome (the header wallet chip) is present. */
export async function expectSignedIn(page: Page): Promise<void> {
  await expect(page.locator('.wallet-chip')).toBeVisible();
}
