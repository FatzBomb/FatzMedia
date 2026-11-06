/* The app's Java VM on Android (net_https_android.c): HTTPS and text shaping call
 * the platform's Java classes through it. */
#ifndef FA_ANDROID_JNI_H
#define FA_ANDROID_JNI_H

#include <jni.h>

/* The calling thread's JNIEnv, attaching the thread to the VM if needed (it is
 * detached when the thread ends). NULL when the process has no VM (a command-line
 * program) or attaching failed. */
JNIEnv* fa_android_env(void);

/* 1 (after clearing it) if a Java exception is pending, else 0. */
static inline int fa_jni_clear_exception(JNIEnv* env)
{
    if (!(*env)->ExceptionCheck(env)) return 0;
    (*env)->ExceptionClear(env);
    return 1;
}

/* A global reference to the named class; NULL (exception cleared) if it is not found. */
static inline jclass fa_jni_global_class(JNIEnv* env, const char* name)
{
    jclass local = (*env)->FindClass(env, name), g;
    if (!local) {
        fa_jni_clear_exception(env);
        return NULL;
    }
    g = (jclass)(*env)->NewGlobalRef(env, local);
    (*env)->DeleteLocalRef(env, local);
    return g;
}

#endif
