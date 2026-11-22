import { createKatanaApi } from './editor/client.ts';

export const api = createKatanaApi({ getUrl: path => `/api${path}` });
