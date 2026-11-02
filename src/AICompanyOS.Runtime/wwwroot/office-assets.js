const roles = ['ba', 'leader', 'dev', 'tester', 'soc', 'qa', 'subpm', 'owner'];
export const teamAtlas = '/assets/office/team-atlas.png';
export const decorAtlas = '/assets/office/decor-atlas.png';
export const avatarIndex = role => Math.max(0, roles.indexOf(role));
export function portrait(role, className = '') {
  const index = avatarIndex(role);
  return `<span class="agent-portrait ${className}" aria-hidden="true"><svg viewBox="${index % 4 * 512} ${Math.floor(index / 4) * 512} 512 380"><image href="${teamAtlas}" width="2048" height="1024" preserveAspectRatio="none"/></svg></span>`;
}
