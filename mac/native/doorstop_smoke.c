#include <dlfcn.h>
#include <stdio.h>
#include <unistd.h>
int main(int argc,char **argv) {
  if (argc!=2) return 2;
  void *lib=dlopen(argv[1],RTLD_NOW|RTLD_GLOBAL);
  if (!lib) { puts(dlerror()); return 3; }
  void *(*init)(const char*)=(void*(*)(const char*))dlsym(lib,"il2cpp_init");
  if (!init) return 4;
  init("JibbersMacPackagingSmoke");
  sleep(2);
  return 0;
}
